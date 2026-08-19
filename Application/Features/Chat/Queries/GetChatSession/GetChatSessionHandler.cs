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

    public GetChatSessionHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
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
        var level = RoleplayPrompts.CalculateRelationshipLevel(affection);
        string stageName = RoleplayPrompts.GetLevelName(level);
        if (!string.IsNullOrWhiteSpace(character.CustomMilestonesJson))
        {
            try
            {
                var customMilestones = System.Text.Json.JsonSerializer.Deserialize<List<RelationshipMilestoneDto>>(character.CustomMilestonesJson);
                var matched = customMilestones?.FirstOrDefault(ms => affection >= ms.MinScore && affection <= ms.MaxScore);
                if (matched != null)
                {
                    stageName = matched.Name;
                }
            }
            catch
            {
                // Fallback to default
            }
        }

        var messages = session.Messages.Select(m => new ChatMessageDto(
            m.Id,
            m.Role,
            m.Content,
            m.CreatedAt
        )).ToList();

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
            eventsDto
        );

        return Result<ChatSessionDto>.Success(dto);
    }
}
