using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.LLM.Prompts;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Commands.CreateChatSession;

public sealed class CreateChatSessionHandler : IRequestHandler<CreateChatSessionCommand, Result<ChatSessionDto>>
{
    private readonly IUnitOfWork _unitOfWork;

    public CreateChatSessionHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<ChatSessionDto>> Handle(CreateChatSessionCommand command, CancellationToken cancellationToken)
    {
        var characterRepo = _unitOfWork.GetRepository<Character>();
        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();

        var character = await characterRepo.GetByIdAsync(command.Request.CharacterId, cancellationToken);
        if (character == null)
        {
            return Result<ChatSessionDto>.Failure(StatusCodes.Status404NotFound, $"Character with ID '{command.Request.CharacterId}' was not found.");
        }

        var userId = command.Request.UserId;
        var title = string.IsNullOrWhiteSpace(command.Request.Title)
            ? $"Chat with {character.Name}"
            : command.Request.Title;

        var session = new ChatSession(
            character.Id,
            userId,
            title);

        if (!string.IsNullOrWhiteSpace(character.Greeting))
        {
            session.AddAssistantMessage(character.Greeting);
        }

        await sessionRepo.AddAsync(session, cancellationToken);

        // Fetch or create CharacterRelationship for persistent state
        CharacterRelationship? relationship = null;
        if (userId.HasValue && userId.Value != Guid.Empty)
        {
            var defaultMood = Enum.TryParse<CharacterMood>(character.DefaultMood, true, out var dm)
                ? dm
                : CharacterMood.Neutral;

            relationship = await _unitOfWork.Relationships.GetOrCreateAsync(
                userId.Value,
                character.Id,
                character.DefaultAffectionScore,
                defaultMood,
                cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var messages = session.Messages.Select(m => new ChatMessageDto(
            m.Id,
            m.Role,
            m.Content,
            m.CreatedAt
        )).ToList();

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
