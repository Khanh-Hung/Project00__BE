using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.Common;
using Application.DTOs;
using Domain.Entities;
using Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Http;
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
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
        _logger = logger;
    }

    public async Task<Result<TriggerSceneImageResponse>> Handle(TriggerTurnSceneImageGenerationCommand command, CancellationToken cancellationToken)
    {
        var turnRepo = _unitOfWork.GetRepository<CharacterTurn>();

        // 1. Fetch CharacterTurn by TurnId and SessionId
        var turn = await turnRepo.GetAsync(
            t => t.TurnId == command.TurnId && t.SessionId == command.SessionId,
            cancellationToken);

        if (turn == null)
        {
            return Result<TriggerSceneImageResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"Turn '{command.TurnId}' in session '{command.SessionId}' was not found.");
        }

        // 2. Validate Ownership if user is authenticated
        if (!string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) && Guid.TryParse(_currentUserProvider.CurrentUserId, out var uid))
        {
            if (turn.UserId != Guid.Empty && turn.UserId != uid)
            {
                return Result<TriggerSceneImageResponse>.Failure(
                    StatusCodes.Status403Forbidden,
                    "You do not have permission to generate images for this turn.");
            }
        }

        // 3. Ensure VisualSnapshot was frozen and preserved on the turn
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

        // 4. Generate a NEW GenerationRequestId (guaranteeing unique identity for regenerations)
        var generationRequestId = Guid.NewGuid();

        // 5. Enqueue Outbox Message with the FROZEN VisualSnapshot
        var scenePayload = new SceneImageGenerationOutboxPayload(
            TurnId: turn.TurnId,
            CharacterId: turn.CharacterId,
            UserId: turn.UserId,
            Snapshot: snapshot,
            GenerationRequestId: generationRequestId
        );

        var outboxRepo = _unitOfWork.GetRepository<OutboxMessage>();
        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(scenePayload)
        );

        await outboxRepo.AddAsync(outboxMessage, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Enqueued async scene image generation: SessionId={SessionId}, TurnId={TurnId}, GenerationRequestId={GenerationRequestId}",
            command.SessionId, command.TurnId, generationRequestId);

        // 6. Return 202 Accepted with response DTO
        var response = new TriggerSceneImageResponse(
            GenerationRequestId: generationRequestId,
            TurnId: command.TurnId,
            Status: "queued"
        );

        return Result<TriggerSceneImageResponse>.Success(response, StatusCodes.Status202Accepted);
    }
}
