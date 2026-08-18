using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;

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

        var messages = session.Messages.Select(m => new ChatMessageDto(
            m.Id,
            m.Role,
            m.Content,
            m.CreatedAt
        )).ToList();

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
            character.Category
        );

        return Result<ChatSessionDto>.Success(dto);
    }
}
