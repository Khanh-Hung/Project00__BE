using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.LLM.Prompts;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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

        // 1. Retrieve or initialize CharacterRelationship (Single Source of Truth for State)
        CharacterRelationship? relationship = null;
        if (session.UserId.HasValue && session.UserId.Value != Guid.Empty)
        {
            var defaultMood = Enum.TryParse<CharacterMood>(character.DefaultMood, true, out var dm)
                ? dm
                : CharacterMood.Neutral;

            relationship = await _unitOfWork.Relationships.GetOrCreateAsync(
                session.UserId.Value,
                character.Id,
                character.DefaultAffectionScore,
                defaultMood,
                cancellationToken);

            // Soften transient mood after > 24 hours of inactivity
            relationship.SoftenMoodIfInactive(Clock.Now, TimeSpan.FromHours(24), defaultMood);
        }

        // 2. Retrieve diversity-balanced relevant memories for (UserId, CharacterId)
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
                _logger.LogWarning(
                    ex,
                    "Memory retrieval failed for Character {CharacterId}, User {UserId}. Proceeding without memories.",
                    character.Id,
                    session.UserId.Value);
            }
        }

        // 3. Append User Message to session
        var userMsg = session.AddUserMessage(req.Content);

        // 4. Single-Turn AI Roleplay with Blueprint, Relationship State & Memories
        var aiTurn = await _llmService.GenerateRoleplayTurnAsync(
            character,
            session.Messages,
            req.Content,
            relationship,
            relevantMemories,
            cancellationToken);

        // 5. Backend Validates & Mutates Relationship State
        var oldLevel = RoleplayPrompts.CalculateRelationshipLevel(relationship?.AffectionScore ?? character.DefaultAffectionScore);
        int actualDelta = aiTurn.AffectionDelta;
        int newScore = relationship?.AffectionScore ?? character.DefaultAffectionScore;
        RelationshipEventDto? unlockedEventDto = null;

        if (relationship != null)
        {
            var (_, scoreAfterDelta, deltaApplied) = relationship.ApplyAffectionDelta(aiTurn.AffectionDelta);
            newScore = scoreAfterDelta;
            actualDelta = deltaApplied;

            relationship.UpdateMood(aiTurn.Mood, aiTurn.MoodIntensity);

            if (aiTurn.Event != null && !string.IsNullOrWhiteSpace(aiTurn.Event.Key))
            {
                var unlocked = relationship.TryUnlockEvent(aiTurn.Event.Key, aiTurn.Event.Context);
                if (unlocked)
                {
                    unlockedEventDto = new RelationshipEventDto(
                        aiTurn.Event.Key,
                        aiTurn.Event.Context,
                        Clock.Now);
                }
            }
        }

        var newLevel = RoleplayPrompts.CalculateRelationshipLevel(newScore);
        var isLevelUp = newLevel > oldLevel;

        // Compute Display Stage Name
        string stageName = RoleplayPrompts.GetLevelName(newLevel);
        if (!string.IsNullOrWhiteSpace(character.CustomMilestonesJson))
        {
            try
            {
                var customMilestones = System.Text.Json.JsonSerializer.Deserialize<List<RelationshipMilestoneDto>>(character.CustomMilestonesJson);
                var matched = customMilestones?.FirstOrDefault(ms => newScore >= ms.MinScore && newScore <= ms.MaxScore);
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

        // 6. Append AI Assistant Message
        var assistantMsg = session.AddAssistantMessage(aiTurn.Reply);

        // 7. Persist both Session and Relationship state changes
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // 8. Non-blocking trigger notification for background memory extraction
        if (session.UserId.HasValue && session.UserId.Value != Guid.Empty)
        {
            try
            {
                var userMessageCount = session.Messages.Count(m => m.Role == MessageRole.User);
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
            newScore,
            newLevel,
            stageName,
            relationship?.CurrentMood.ToString() ?? character.DefaultMood ?? "Neutral",
            relationship?.MoodIntensity ?? 20,
            actualDelta,
            isLevelUp,
            unlockedEventDto
        );

        return Result<SendMessageResponse>.Success(response);
    }
}
