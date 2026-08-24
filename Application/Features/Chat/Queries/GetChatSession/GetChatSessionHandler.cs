using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.LLM.Prompts;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Queries.GetChatSession;

public sealed class GetChatSessionHandler : IRequestHandler<GetChatSessionQuery, Result<ChatSessionDto>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetChatSessionHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public async Task<Result<ChatSessionDto>> Handle(GetChatSessionQuery query, CancellationToken cancellationToken)
    {
        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();
        var characterRepo = _unitOfWork.GetRepository<Character>();

        var session = await sessionRepo.GetByIdAsync(query.SessionId, cancellationToken);
        if (session == null)
        {
            return Result<ChatSessionDto>.Failure(StatusCodes.Status404NotFound, $"Chat session '{query.SessionId}' was not found.");
        }

        var currentUserId = _currentUserProvider.CurrentUserId;
        if (session.UserId.HasValue && session.UserId.Value != Guid.Empty)
        {
            if (!string.IsNullOrEmpty(currentUserId) && Guid.TryParse(currentUserId, out var uid))
            {
                if (session.UserId.Value != uid)
                {
                    return Result<ChatSessionDto>.Failure(StatusCodes.Status403Forbidden, "You do not have access to this chat session.");
                }
            }
        }

        var character = await characterRepo.GetByIdAsync(session.CharacterId, cancellationToken);
        if (character == null)
        {
            return Result<ChatSessionDto>.Failure(StatusCodes.Status404NotFound, $"Character for this session was not found.");
        }

        CharacterRelationship? relationship = null;
        if (session.UserId.HasValue && session.UserId.Value != Guid.Empty)
        {
            relationship = await _unitOfWork.Relationships.GetByPairAsync(session.UserId.Value, character.Id, cancellationToken);
        }

        var affection = relationship?.AffectionScore ?? character.DefaultAffectionScore;
        var (level, stageName, _) = Application.Common.RelationshipStageResolver.Resolve(affection, character.CustomMilestonesJson);

        var turnRepo = _unitOfWork.GetRepository<CharacterTurn>();
        var sceneImageRepo = _unitOfWork.GetRepository<SceneImage>();
        var jobRepo = _unitOfWork.GetRepository<ImageGenerationJob>();

        var assistantMsgIds = session.Messages
            .Where(m => m.Role == MessageRole.Assistant)
            .Select(m => m.Id)
            .ToHashSet();

        List<CharacterTurn> turns = new();
        if (assistantMsgIds.Count > 0)
        {
            turns = (await turnRepo.GetAllAsync(
                t => t.SessionId == session.Id && assistantMsgIds.Contains(t.AssistantMessageId),
                cancellationToken)).ToList();
        }

        var turnIds = turns.Select(t => t.TurnId).ToHashSet();

        List<SceneImage> currentSceneImages = new();
        List<ImageGenerationJob> recentJobs = new();

        if (turnIds.Count > 0)
        {
            currentSceneImages = (await sceneImageRepo.GetAllAsync(
                i => i.SessionId == session.Id && i.IsCurrent && turnIds.Contains(i.TurnId),
                cancellationToken)).ToList();

            recentJobs = (await jobRepo.GetAllAsync(
                j => j.SessionId == session.Id && turnIds.Contains(j.TurnId) &&
                     (j.Status == ImageJobStatus.Pending || j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Failed),
                cancellationToken)).ToList();
        }

        var currentImagesByTurn = currentSceneImages
            .GroupBy(i => i.TurnId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First());

        var jobsByTurn = recentJobs
            .GroupBy(j => j.TurnId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var turnsByAssistantMsgId = turns
            .GroupBy(t => t.AssistantMessageId)
            .ToDictionary(g => g.Key, g => g.First());

        var messages = session.Messages.Select(m =>
        {
            Guid? turnId = null;
            string? sceneImageUrl = null;
            string? sceneImageStatus = null;
            Guid? genReqId = null;

            if (m.Role == MessageRole.Assistant)
            {
                if (turnsByAssistantMsgId.TryGetValue(m.Id, out var turn))
                {
                    turnId = turn.TurnId;
                    var hasCurrentImage = currentImagesByTurn.TryGetValue(turn.TurnId, out var img);
                    var turnJobs = jobsByTurn.TryGetValue(turn.TurnId, out var jobList) ? jobList : new List<ImageGenerationJob>();

                    // 1. In-flight jobs (Pending or Processing) ALWAYS take highest precedence for active polling & live state
                    var activeJob = turnJobs
                        .Where(j => j.Status == ImageJobStatus.Pending || j.Status == ImageJobStatus.Processing)
                        .OrderByDescending(j => j.CreatedAt)
                        .FirstOrDefault();

                    if (activeJob != null)
                    {
                        sceneImageStatus = SceneImageStatuses.FromJobStatus(activeJob.Status);
                        genReqId = activeJob.GenerationRequestId;
                        sceneImageUrl = hasCurrentImage ? img!.ImageUrl : null;
                    }
                    else if (hasCurrentImage)
                    {
                        // 2. No active in-flight job, but a completed current image exists
                        sceneImageUrl = img!.ImageUrl;
                        sceneImageStatus = SceneImageStatuses.Completed;
                        genReqId = img.GenerationRequestId;
                    }
                    else
                    {
                        // 3. No active job and no completed image; check if a failed job occurred
                        var failedJob = turnJobs
                            .Where(j => j.Status == ImageJobStatus.Failed)
                            .OrderByDescending(j => j.CreatedAt)
                            .FirstOrDefault();

                        if (failedJob != null)
                        {
                            sceneImageStatus = SceneImageStatuses.Failed;
                            genReqId = failedJob.GenerationRequestId;
                        }
                    }
                }
            }

            return new ChatMessageDto(
                m.Id,
                m.Role,
                m.Content,
                m.CreatedAt,
                TurnId: turnId,
                SceneImageUrl: sceneImageUrl,
                SceneImageStatus: sceneImageStatus,
                GenerationRequestId: genReqId
            );
        }).ToList();

        var eventsDto = relationship?.Events.Select(e => new RelationshipEventDto(e.EventKey, e.Context, e.UnlockedAt)).ToList();

        var dto = new ChatSessionDto(
            session.Id,
            session.CharacterId,
            character.Name,
            character.AvatarUrl,
            session.Title,
            messages,
            session.CreatedAt,
            character.Title,
            character.PersonalityPrompt,
            character.Category,
            affection,
            level,
            stageName,
            relationship?.CurrentMood.ToString() ?? character.DefaultMood ?? "Neutral",
            relationship?.MoodIntensity ?? 20,
            eventsDto,
            session.Status,
            session.WalkOutReason,
            session.WalkedOutAt
        );

        return Result<ChatSessionDto>.Success(dto);
    }
}
