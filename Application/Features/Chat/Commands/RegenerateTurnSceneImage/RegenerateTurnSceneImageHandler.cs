using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Events;
using Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Application.Features.Chat.Commands.RegenerateTurnSceneImage;

public sealed class RegenerateTurnSceneImageHandler : IRequestHandler<RegenerateTurnSceneImageCommand, Result<TriggerSceneImageResponse>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly IVisualPredecessorResolver _predecessorResolver;
    private readonly ILogger<RegenerateTurnSceneImageHandler> _logger;

    public RegenerateTurnSceneImageHandler(
        IUnitOfWork unitOfWork,
        ICurrentUserProvider currentUserProvider,
        IVisualPredecessorResolver predecessorResolver,
        ILogger<RegenerateTurnSceneImageHandler> logger)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _currentUserProvider = currentUserProvider ?? throw new ArgumentNullException(nameof(currentUserProvider));
        _predecessorResolver = predecessorResolver ?? throw new ArgumentNullException(nameof(predecessorResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result<TriggerSceneImageResponse>> Handle(RegenerateTurnSceneImageCommand command, CancellationToken cancellationToken)
    {
        // 1. Resolve Authenticated User
        if (string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) || !Guid.TryParse(_currentUserProvider.CurrentUserId, out var currentUserId))
        {
            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status401Unauthorized,
                "Authentication is required to regenerate scene images.");
        }

        var turnRepo = _unitOfWork.GetRepository<CharacterTurn>();

        // 2. Fetch CharacterTurn
        var turn = await turnRepo.GetAsync(
            t => t.TurnId == command.TurnId && t.SessionId == command.SessionId,
            cancellationToken);

        if (turn == null)
        {
            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"Turn '{command.TurnId}' in session '{command.SessionId}' was not found.");
        }

        // 3. Strict Ownership Authorization
        if (turn.UserId != currentUserId)
        {
            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status403Forbidden,
                "You do not have permission to regenerate images for this turn.");
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

        // 5. Idempotency Check for explicit RequestId
        var targetRequestId = command.RequestId ?? Guid.NewGuid();
        var jobRepo = _unitOfWork.GetRepository<ImageGenerationJob>();

        var existingJob = await jobRepo.GetAsync(
            j => j.SessionId == command.SessionId && j.TurnId == command.TurnId && j.GenerationRequestId == targetRequestId,
            cancellationToken);

        if (existingJob != null)
        {
            _logger.LogInformation("Idempotent regeneration request hit: SessionId={SessionId}, TurnId={TurnId}, RequestId={RequestId}, Status={Status}",
                command.SessionId, command.TurnId, targetRequestId, existingJob.Status);

            return Result<TriggerSceneImageResponse>.Success(new TriggerSceneImageResponse(
                GenerationRequestId: targetRequestId,
                TurnId: command.TurnId,
                Status: existingJob.Status.ToString().ToLowerInvariant()
            ), StatusCodes.Status200OK);
        }

        // 6. Authoritatively resolve current predecessor for this regeneration attempt
        var resolvedPredecessor = await _predecessorResolver.ResolveAsync(command.SessionId, command.TurnId, snapshot, cancellationToken);
        
        var effectiveSnapshot = snapshot;
        if (resolvedPredecessor != null && !string.IsNullOrWhiteSpace(resolvedPredecessor.ImageUrl))
        {
            effectiveSnapshot = snapshot with
            {
                PreviousSceneImageUrl = resolvedPredecessor.ImageUrl,
                PredecessorSceneImageId = resolvedPredecessor.ArtifactId,
                PredecessorSceneRevision = resolvedPredecessor.VisualRevision
            };
        }

        // 7. Find previous job for lineage event emission
        var previousJob = await jobRepo.GetAsync(
            j => j.SessionId == command.SessionId && j.TurnId == command.TurnId,
            cancellationToken);

        // 8. Enqueue Outbox Message
        var scenePayload = new SceneImageGenerationOutboxPayload(
            TurnId: turn.TurnId,
            CharacterId: turn.CharacterId,
            UserId: turn.UserId,
            Snapshot: effectiveSnapshot,
            GenerationRequestId: targetRequestId
        );

        var outboxRepo = _unitOfWork.GetRepository<OutboxMessage>();
        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(scenePayload)
        );

        await outboxRepo.AddAsync(outboxMessage, cancellationToken);

        if (previousJob != null)
        {
            var regenEvent = new VisualGenerationRegenerated(
                SessionId: command.SessionId,
                TurnId: command.TurnId,
                PreviousJobId: previousJob.Id,
                NewJobId: targetRequestId,
                SceneRevision: snapshot.SceneRevision,
                OccurredAt: DateTime.UtcNow
            );

            await outboxRepo.AddAsync(new OutboxMessage(
                eventType: "VisualGenerationRegenerated",
                payloadJson: JsonSerializer.Serialize(regenEvent)
            ), cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Enqueued visual regeneration request: SessionId={SessionId}, TurnId={TurnId}, GenerationRequestId={GenerationRequestId}",
            command.SessionId, command.TurnId, targetRequestId);

        return Result<TriggerSceneImageResponse>.Success(new TriggerSceneImageResponse(
            GenerationRequestId: targetRequestId,
            TurnId: command.TurnId,
            Status: "queued"
        ), StatusCodes.Status202Accepted);
    }
}
