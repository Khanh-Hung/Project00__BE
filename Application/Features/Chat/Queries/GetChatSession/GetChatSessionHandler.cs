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

        var turns = await turnRepo.GetAllAsync(t => t.SessionId == session.Id, cancellationToken);
        var sceneImages = await sceneImageRepo.GetAllAsync(i => i.SessionId == session.Id, cancellationToken);
        var activeJobs = await jobRepo.GetAllAsync(j => j.SessionId == session.Id && (j.Status == ImageJobStatus.Pending || j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Failed), cancellationToken);

        var currentImagesByTurn = sceneImages
            .Where(i => i.IsCurrent)
            .GroupBy(i => i.TurnId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First());

        var activeJobsByTurn = activeJobs
            .GroupBy(j => j.TurnId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedAt).First());

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
                    if (currentImagesByTurn.TryGetValue(turn.TurnId, out var img))
                    {
                        sceneImageUrl = img.ImageUrl;
                        sceneImageStatus = "completed";
                        genReqId = img.GenerationRequestId;
                    }
                    else if (activeJobsByTurn.TryGetValue(turn.TurnId, out var job))
                    {
                        sceneImageStatus = job.Status switch
                        {
                            ImageJobStatus.Processing => "processing",
                            ImageJobStatus.Pending => "pending",
                            ImageJobStatus.Failed => "failed",
                            _ => null
                        };
                        genReqId = job.GenerationRequestId;
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
