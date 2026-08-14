using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Commands.SendChatMessage;

public sealed class SendChatMessageHandler : IRequestHandler<SendChatMessageCommand, Result<SendMessageResponse>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILLMService _llmService;

    public SendChatMessageHandler(IUnitOfWork unitOfWork, ILLMService llmService)
    {
        _unitOfWork = unitOfWork;
        _llmService = llmService;
    }

    public async Task<Result<SendMessageResponse>> Handle(SendChatMessageCommand command, CancellationToken cancellationToken)
    {
        var req = command.Request;
        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();
        var characterRepo = _unitOfWork.GetRepository<Character>();

        var session = await sessionRepo.GetByIdAsync(req.SessionId, cancellationToken);
        if (session == null)
        {
            return Result<SendMessageResponse>.Failure(StatusCodes.Status404NotFound, $"Chat session with ID '{req.SessionId}' was not found.");
        }

        var character = await characterRepo.GetByIdAsync(session.CharacterId, cancellationToken);
        if (character == null)
        {
            return Result<SendMessageResponse>.Failure(StatusCodes.Status404NotFound, $"Character for this session was not found.");
        }

        var userMsg = session.AddUserMessage(req.Content);

        var aiResponseText = await _llmService.GenerateRoleplayResponseAsync(
            character,
            session.Messages,
            req.Content,
            cancellationToken);

        var assistantMsg = session.AddAssistantMessage(aiResponseText);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var response = new SendMessageResponse(
            new ChatMessageDto(userMsg.Id, userMsg.Role, userMsg.Content, userMsg.CreatedAt),
            new ChatMessageDto(assistantMsg.Id, assistantMsg.Role, assistantMsg.Content, assistantMsg.CreatedAt)
        );

        return Result<SendMessageResponse>.Success(response);
    }
}
