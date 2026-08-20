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

        // 1. Check if an active session already exists for this (UserId, CharacterId) pair
        ChatSession? session = null;
        if (userId.HasValue && userId.Value != Guid.Empty)
        {
            var userSessions = await sessionRepo.GetAllAsync(
                s => s.UserId == userId.Value && s.CharacterId == character.Id && s.Status != SessionStatus.WalkedOut,
                cancellationToken);

            session = userSessions.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        }

        // 2. If no active session exists, create a new unique session
        if (session == null)
        {
            session = new ChatSession(
                character.Id,
                userId,
                title);

            await sessionRepo.AddAsync(session, cancellationToken);
        }

        // 3. Fetch or create CharacterRelationship for persistent state
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
        var (level, stageName, _) = Application.Common.RelationshipStageResolver.Resolve(affection, character.CustomMilestonesJson);

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
