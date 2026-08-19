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
    private readonly IMemoryService _memoryService;
    private readonly IMemoryExtractionTrigger _extractionTrigger;
    private readonly ILogger<SendChatMessageHandler> _logger;

    public SendChatMessageHandler(
        IUnitOfWork unitOfWork,
        ILLMService llmService,
        IMemoryService memoryService,
        IMemoryExtractionTrigger extractionTrigger,
        ILogger<SendChatMessageHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _llmService = llmService;
        _memoryService = memoryService;
        _extractionTrigger = extractionTrigger;
        _logger = logger;
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

        // 1. Retrieve diversity-balanced relevant memories for (UserId, CharacterId)
        IReadOnlyList<CharacterMemory>? relevantMemories = null;
        if (session.UserId.HasValue && session.UserId.Value != Guid.Empty)
        {
            try
            {
                relevantMemories = await _memoryService.GetRelevantMemoriesAsync(
                    session.UserId.Value,
                    character.Id,
                    maxCount: 6,
                    ct: cancellationToken);
            }
            catch (Exception ex)
            {
                // Observability: Log degraded memory state without failing the chat response
                _logger.LogWarning(
                    ex,
                    "Memory retrieval failed for Character {CharacterId}, User {UserId}. Proceeding with chat response without memories.",
                    character.Id,
                    session.UserId.Value);
            }
        }

        // 2. Append User Message
        var userMsg = session.AddUserMessage(req.Content);

        // 3. AI Roleplay & Real-time Emotion / Affection Analysis (with Blueprint, State, and Memories)
        var aiTurn = await _llmService.GenerateRoleplayTurnAsync(
            character,
            session.Messages,
            req.Content,
            session,
            relevantMemories,
            cancellationToken);

        // 4. Update Real Affection Score & Mood evaluated dynamically by Gemini AI
        var (newScore, newLevel, actualDelta, isLevelUp) = session.UpdateAffection(aiTurn.AffectionDelta, aiTurn.Mood);

        // 5. Append AI Assistant Message
        var assistantMsg = session.AddAssistantMessage(aiTurn.Reply);

        // 6. Persist real state to Database
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // 7. Non-blocking trigger notification for background memory extraction
        if (session.UserId.HasValue && session.UserId.Value != Guid.Empty)
        {
            try
            {
                var userMessageCount = session.Messages.Count(m => m.Role == Domain.Enums.MessageRole.User);
                var recentMessageDtos = session.Messages
                    .TakeLast(10)
                    .Select(m => new ChatMessageDto(m.Id, m.Role, m.Content, m.CreatedAt))
                    .ToList();

                _extractionTrigger.NotifyMessageSent(new MemoryExtractionJob(
                    session.Id,
                    character.Id,
                    session.UserId.Value,
                    recentMessageDtos,
                    userMessageCount));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Memory extraction trigger notification failed for Session {SessionId}.", session.Id);
            }
        }

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
