using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.Common;
using Application.DTOs;
using Domain.Entities;
using Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Application.Features.Chat.Queries.GetSceneImageStatus;

public sealed class GetSceneImageStatusHandler : IRequestHandler<GetSceneImageStatusQuery, Result<SceneImageStatusResponse>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<GetSceneImageStatusHandler> _logger;

    public GetSceneImageStatusHandler(
        IUnitOfWork unitOfWork,
        ICurrentUserProvider currentUserProvider,
        ILogger<GetSceneImageStatusHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
        _logger = logger;
    }

    public async Task<Result<SceneImageStatusResponse>> Handle(GetSceneImageStatusQuery query, CancellationToken cancellationToken)
    {
        // 1. Resolve Authenticated User
        if (string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) || !Guid.TryParse(_currentUserProvider.CurrentUserId, out var currentUserId))
        {
            return Result<SceneImageStatusResponse>.Failure(
                StatusCodes.Status401Unauthorized,
                "Authentication is required to query scene image status.");
        }

        var sceneImageRepo = _unitOfWork.GetRepository<SceneImage>();
        var jobRepo = _unitOfWork.GetRepository<ImageGenerationJob>();
        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();
        var outboxRepo = _unitOfWork.GetRepository<OutboxMessage>();

        // 2. Check if image generation already completed and SceneImage is stored
        var sceneImage = await sceneImageRepo.GetAsync(
            i => i.GenerationRequestId == query.GenerationRequestId,
            cancellationToken);

        if (sceneImage != null)
        {
            var session = await sessionRepo.GetByIdAsync(sceneImage.SessionId, cancellationToken);
            if (session == null)
            {
                return Result<SceneImageStatusResponse>.Failure(
                    StatusCodes.Status404NotFound,
                    "Chat session for this generation request was not found.");
            }

            // Strict Fail-Closed Authorization: Session.UserId MUST exist, not be Guid.Empty, and equal CurrentUserId
            if (!session.UserId.HasValue || session.UserId.Value == Guid.Empty || session.UserId.Value != currentUserId)
            {
                return Result<SceneImageStatusResponse>.Failure(
                    StatusCodes.Status403Forbidden,
                    "You do not have access to this generation request.");
            }

            return Result<SceneImageStatusResponse>.Success(new SceneImageStatusResponse(
                GenerationRequestId: sceneImage.GenerationRequestId,
                TurnId: sceneImage.TurnId,
                SessionId: sceneImage.SessionId,
                Status: "completed",
                ImageUrl: sceneImage.ImageUrl,
                SceneRevision: sceneImage.SceneRevision,
                Prompt: sceneImage.Prompt,
                CreatedAt: sceneImage.CreatedAt
            ));
        }

        // 3. Check if active or failed ImageGenerationJob exists
        var job = await jobRepo.GetAsync(
            j => j.GenerationRequestId == query.GenerationRequestId,
            cancellationToken);

        if (job != null)
        {
            var session = await sessionRepo.GetByIdAsync(job.SessionId, cancellationToken);
            if (session == null)
            {
                return Result<SceneImageStatusResponse>.Failure(
                    StatusCodes.Status404NotFound,
                    "Chat session for this generation request was not found.");
            }

            // Strict Fail-Closed Authorization: Session.UserId MUST exist, not be Guid.Empty, and equal CurrentUserId
            if (!session.UserId.HasValue || session.UserId.Value == Guid.Empty || session.UserId.Value != currentUserId)
            {
                return Result<SceneImageStatusResponse>.Failure(
                    StatusCodes.Status403Forbidden,
                    "You do not have access to this generation request.");
            }

            var statusStr = job.Status switch
            {
                ImageJobStatus.Completed => "completed",
                ImageJobStatus.Processing => "processing",
                ImageJobStatus.Pending => "pending",
                ImageJobStatus.Failed => "failed",
                ImageJobStatus.Cancelled => "cancelled",
                _ => job.Status.ToString().ToLowerInvariant()
            };

            return Result<SceneImageStatusResponse>.Success(new SceneImageStatusResponse(
                GenerationRequestId: job.GenerationRequestId,
                TurnId: job.TurnId,
                SessionId: job.SessionId,
                Status: statusStr,
                FailureReason: job.FailureReason,
                IsRetryable: job.IsRetryable,
                SceneRevision: job.SceneRevision,
                CreatedAt: job.CreatedAt
            ));
        }

        // 4. Check if request is still queued in durable Outbox table (Direct persistence predicate query)
        var requestIdStr = query.GenerationRequestId.ToString();
        var outboxMessage = await outboxRepo.GetAsync(
            m => m.EventType == OutboxEventTypes.SceneImageGeneration && m.PayloadJson.Contains(requestIdStr),
            cancellationToken);

        if (outboxMessage != null)
        {
            SceneImageGenerationOutboxPayload? payload = null;
            try
            {
                payload = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMessage.PayloadJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse Outbox payload for message {Id}", outboxMessage.Id);
            }

            if (payload != null && payload.GenerationRequestId == query.GenerationRequestId)
            {
                var session = await sessionRepo.GetByIdAsync(payload.Snapshot.SessionId, cancellationToken);
                if (session == null)
                {
                    return Result<SceneImageStatusResponse>.Failure(
                        StatusCodes.Status404NotFound,
                        "Chat session for this generation request was not found.");
                }

                // Strict Fail-Closed Authorization: ChatSession.UserId is the source of truth
                if (!session.UserId.HasValue || session.UserId.Value == Guid.Empty || session.UserId.Value != currentUserId)
                {
                    return Result<SceneImageStatusResponse>.Failure(
                        StatusCodes.Status403Forbidden,
                        "You do not have access to this generation request.");
                }

                // Consistency Invariant: Payload.UserId MUST strictly match Session.UserId
                if (payload.UserId == Guid.Empty || payload.UserId != session.UserId.Value)
                {
                    return Result<SceneImageStatusResponse>.Failure(
                        StatusCodes.Status403Forbidden,
                        "Generation request payload ownership mismatch.");
                }

                // Consistency Invariant: Snapshot identity (TurnId, SessionId, CharacterId) MUST strictly match payload metadata and session
                if (payload.Snapshot.SessionId != session.Id ||
                    payload.Snapshot.TurnId != payload.TurnId ||
                    payload.Snapshot.CharacterId != payload.CharacterId ||
                    session.CharacterId != payload.CharacterId)
                {
                    return Result<SceneImageStatusResponse>.Failure(
                        StatusCodes.Status500InternalServerError,
                        "Frozen snapshot identity mismatch in queued generation request.");
                }

                // Consistency Invariant: Turn entity (if persisted) MUST match Session and Character
                var turnRepo = _unitOfWork.GetRepository<CharacterTurn>();
                var turn = await turnRepo.GetAsync(t => t.TurnId == payload.TurnId, cancellationToken);
                if (turn != null && (turn.SessionId != session.Id || turn.CharacterId != session.CharacterId || turn.UserId != currentUserId))
                {
                    return Result<SceneImageStatusResponse>.Failure(
                        StatusCodes.Status500InternalServerError,
                        "Turn metadata mismatch in queued generation request.");
                }

                return Result<SceneImageStatusResponse>.Success(new SceneImageStatusResponse(
                    GenerationRequestId: payload.GenerationRequestId,
                    TurnId: payload.TurnId,
                    SessionId: payload.Snapshot.SessionId,
                    Status: "queued",
                    SceneRevision: payload.Snapshot.SceneRevision,
                    Prompt: null,
                    CreatedAt: outboxMessage.CreatedAt
                ));
            }
        }

        // 5. Not found anywhere
        return Result<SceneImageStatusResponse>.Failure(
            StatusCodes.Status404NotFound,
            $"Scene image generation request '{query.GenerationRequestId}' was not found.");
    }
}
