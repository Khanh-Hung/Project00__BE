using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
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

        var userId = command.Request.UserId ?? Guid.NewGuid();
        var title = string.IsNullOrWhiteSpace(command.Request.Title)
            ? $"Chat with {character.Name}"
            : command.Request.Title;

        var session = new ChatSession(character.Id, userId, title);

        if (!string.IsNullOrWhiteSpace(character.Greeting))
        {
            session.AddAssistantMessage(character.Greeting);
        }

        await sessionRepo.AddAsync(session, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

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
            session.CreatedAt
        );

        return Result<ChatSessionDto>.Success(dto);
    }
}
