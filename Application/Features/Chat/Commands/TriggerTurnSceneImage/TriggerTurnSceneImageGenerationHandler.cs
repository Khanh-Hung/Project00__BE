using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.Common;
using Application.DTOs;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Features.Chat.Commands.TriggerTurnSceneImage;

public sealed class TriggerTurnSceneImageGenerationHandler : IRequestHandler<TriggerTurnSceneImageGenerationCommand, Result<TriggerSceneImageResponse>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<TriggerTurnSceneImageGenerationHandler> _logger;

    public TriggerTurnSceneImageGenerationHandler(
        IUnitOfWork unitOfWork,
        ICurrentUserProvider currentUserProvider,
        ILogger<TriggerTurnSceneImageGenerationHandler> logger)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _currentUserProvider = currentUserProvider ?? throw new ArgumentNullException(nameof(currentUserProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result<TriggerSceneImageResponse>> Handle(TriggerTurnSceneImageGenerationCommand command, CancellationToken cancellationToken)
    {
        // 1. Resolve Authenticated User (Strict Auth invariant)
        if (string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) || !Guid.TryParse(_currentUserProvider.CurrentUserId, out var currentUserId))
        {
            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status401Unauthorized,
                "Authentication is required to generate scene images.");
        }

        var turnRepo = _unitOfWork.GetRepository<CharacterTurn>();

        // 2. Fetch CharacterTurn by TurnId and SessionId (Session Isolation)
        var turn = await turnRepo.GetAsync(
            t => t.TurnId == command.TurnId && t.SessionId == command.SessionId,
            cancellationToken);

        if (turn == null)
        {
            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"Turn '{command.TurnId}' in session '{command.SessionId}' was not found.");
        }

        // 3. Strict Ownership Authorization: Turn.UserId MUST strictly equal CurrentUserId
        if (turn.UserId != currentUserId)
        {
            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status403Forbidden,
                "You do not have permission to generate images for this turn.");
        }

        // 4. Ensure VisualSnapshot was frozen and preserved on the turn
        if (string.IsNullOrWhiteSpace(turn.VisualSnapshotJson))
        {
            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Visual snapshot is not available for this turn.");
        }

        VisualSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<VisualSnapshot>(turn.VisualSnapshotJson);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize VisualSnapshotJson for TurnId {TurnId}", command.TurnId);
            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status500InternalServerError,
                "Failed to read frozen visual snapshot for this turn.");
        }

        if (snapshot == null)
        {
            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Visual snapshot is invalid or empty for this turn.");
        }

        // 5. Validate Snapshot Identity Invariant (TurnId, SessionId, CharacterId must strictly match Turn entity)
        if (snapshot.TurnId != turn.TurnId || snapshot.SessionId != turn.SessionId || snapshot.CharacterId != turn.CharacterId)
        {
            _logger.LogError(
                "Frozen VisualSnapshot identity mismatch for TurnId {TurnId}: Snapshot(TurnId={SnapTurnId}, SessionId={SnapSessionId}, CharacterId={SnapCharId}) vs Turn(TurnId={TurnId}, SessionId={SessionId}, CharacterId={CharacterId})",
                turn.TurnId, snapshot.TurnId, snapshot.SessionId, snapshot.CharacterId, turn.TurnId, turn.SessionId, turn.CharacterId);

            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status500InternalServerError,
                "Frozen visual snapshot identity does not match turn metadata.");
        }

        var jobRepo = _unitOfWork.GetRepository<ImageGenerationJob>();

        // 6. Fast-path Idempotency Check: If specific RequestId was requested, check if already exists
        var targetRequestId = command.RequestId ?? Guid.NewGuid();

        if (command.RequestId.HasValue)
        {
            var existingJob = await jobRepo.GetAsync(
                j => j.SessionId == command.SessionId && j.TurnId == command.TurnId && j.GenerationRequestId == targetRequestId,
                cancellationToken);

            if (existingJob != null)
            {
                var existingStatus = (existingJob.Status == ImageJobStatus.Pending || existingJob.Status == ImageJobStatus.Queued)
                    ? "queued"
                    : existingJob.Status.ToString().ToLowerInvariant();

                _logger.LogInformation("Idempotent generation request fast-path hit: SessionId={SessionId}, TurnId={TurnId}, RequestId={RequestId}, Status={Status}",
                    command.SessionId, command.TurnId, targetRequestId, existingStatus);

                return Result<TriggerSceneImageResponse>.Success(new TriggerSceneImageResponse(
                    GenerationRequestId: targetRequestId,
                    TurnId: command.TurnId,
                    Status: existingStatus
                ), StatusCodes.Status200OK);
            }
        }

        // 7. Atomic Idempotency Fence: Create Pending ImageGenerationJob and Outbox Message together
        var scenePayload = new SceneImageGenerationOutboxPayload(
            TurnId: turn.TurnId,
            CharacterId: turn.CharacterId,
            UserId: turn.UserId,
            Snapshot: snapshot,
            GenerationRequestId: targetRequestId
        );

        var newJob = new ImageGenerationJob(
            sessionId: turn.SessionId,
            turnId: turn.TurnId,
            characterId: turn.CharacterId,
            sceneRevision: snapshot.SceneRevision,
            generationRequestId: targetRequestId,
            userId: turn.UserId,
            provider: "ComfyUI",
            workflow: snapshot.GenerationProfile?.Workflow ?? "VisualIdentity",
            workflowVersion: snapshot.GenerationProfile?.WorkflowVersion ?? 1,
            generationMetadataJson: JsonSerializer.Serialize(scenePayload)
        );

        var outboxRepo = _unitOfWork.GetRepository<OutboxMessage>();
        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(scenePayload)
        );

        await jobRepo.AddAsync(newJob, cancellationToken);
        await outboxRepo.AddAsync(outboxMessage, cancellationToken);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "[TriggerTurnSceneImageGenerationHandler] Concurrency collision on GenerationRequestId {RequestId}. Reloading existing job.", targetRequestId);

            var reloadedJob = await jobRepo.GetAsync(
                j => j.SessionId == command.SessionId && j.GenerationRequestId == targetRequestId,
                cancellationToken);

            if (reloadedJob != null)
            {
                var reloadedStatus = (reloadedJob.Status == ImageJobStatus.Pending || reloadedJob.Status == ImageJobStatus.Queued)
                    ? "queued"
                    : reloadedJob.Status.ToString().ToLowerInvariant();

                return Result<TriggerSceneImageResponse>.Success(new TriggerSceneImageResponse(
                    GenerationRequestId: targetRequestId,
                    TurnId: command.TurnId,
                    Status: reloadedStatus
                ), StatusCodes.Status200OK);
            }

            throw;
        }

        _logger.LogInformation("Enqueued async scene image generation: SessionId={SessionId}, TurnId={TurnId}, GenerationRequestId={GenerationRequestId}",
            command.SessionId, command.TurnId, targetRequestId);

        // 8. Return 202 Accepted with response DTO
        var response = new TriggerSceneImageResponse(
            GenerationRequestId: targetRequestId,
            TurnId: command.TurnId,
            Status: "queued"
        );

        return Result<TriggerSceneImageResponse>.Success(response, StatusCodes.Status202Accepted);
    }
}
