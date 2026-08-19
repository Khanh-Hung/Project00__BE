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

        // 1. Append User Message
        var userMsg = session.AddUserMessage(req.Content);

        // 2. AI Roleplay & Real-time Emotion / Affection Analysis
        var aiTurn = await _llmService.GenerateRoleplayTurnAsync(
            character,
            session.Messages,
            req.Content,
            session,
            cancellationToken);

        // 3. Update Real Affection Score & Mood evaluated dynamically by Gemini AI
        var (newScore, newLevel, actualDelta, isLevelUp) = session.UpdateAffection(aiTurn.AffectionDelta, aiTurn.Mood);

        // 4. Append AI Assistant Message
        var assistantMsg = session.AddAssistantMessage(aiTurn.Reply);

        // 5. Persist real state to Database
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var response = new SendMessageResponse(
            new ChatMessageDto(userMsg.Id, userMsg.Role, userMsg.Content, userMsg.CreatedAt),
            new ChatMessageDto(assistantMsg.Id, assistantMsg.Role, assistantMsg.Content, assistantMsg.CreatedAt),
            AffectionScore: session.AffectionScore,
            RelationshipLevel: session.RelationshipLevel,
            CurrentMood: session.CurrentMood,
            AffectionDelta: actualDelta,
            LevelUp: isLevelUp
        );

        return Result<SendMessageResponse>.Success(response);
    }
}
