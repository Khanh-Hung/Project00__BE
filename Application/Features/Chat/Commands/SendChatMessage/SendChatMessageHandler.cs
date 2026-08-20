using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Application.Features.Chat.Commands.SendChatMessage;

public sealed class SendChatMessageHandler : IRequestHandler<SendChatMessageCommand, Result<SendMessageResponse>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILLMService _llmService;
    private readonly IRoleplayContextEngine _contextEngine;
    private readonly IMemoryExtractionTrigger _extractionTrigger;
    private readonly ILogger<SendChatMessageHandler> _logger;

    public SendChatMessageHandler(
        IUnitOfWork unitOfWork,
        ILLMService llmService,
        IRoleplayContextEngine contextEngine,
        IMemoryExtractionTrigger extractionTrigger,
        ILogger<SendChatMessageHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _llmService = llmService;
        _contextEngine = contextEngine;
        _extractionTrigger = extractionTrigger;
        _logger = logger;
    }

    public async Task<Result<SendMessageResponse>> Handle(SendChatMessageCommand command, CancellationToken cancellationToken)
    {
        var req = command.Request;

        // 1. Build Isolated & Budget-Constrained Roleplay Context via Context Engine
        RoleplayContext context;
        try
        {
            context = await _contextEngine.BuildContextAsync(req.SessionId, req.Content, ct: cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return Result<SendMessageResponse>.Failure(StatusCodes.Status404NotFound, ex.Message);
        }

        var session = context.Session;
        var character = context.Character;
        var relationship = context.Relationship;

        // 2. Append User Message to session
        var userMsg = session.AddUserMessage(req.Content);

        // 3. Single-Turn AI Roleplay with 6-Layer Compiled Context
        var aiTurn = await _llmService.GenerateRoleplayTurnAsync(context, cancellationToken);

        // 4. Backend Validates & Mutates Dynamic Relationship State
        var (oldLevel, _, _) = RelationshipStageResolver.Resolve(relationship?.AffectionScore ?? character.DefaultAffectionScore, character.CustomMilestonesJson);
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

        var (newLevel, stageName, _) = RelationshipStageResolver.Resolve(newScore, character.CustomMilestonesJson);
        var isLevelUp = newLevel > oldLevel;

        // 5. Append AI Assistant Message
        var assistantMsg = session.AddAssistantMessage(aiTurn.Reply);

        // 6. Persist changes to Database
        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();
        sessionRepo.Update(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // 7. Non-blocking Asynchronous Trigger for Long-term Memory Extraction
        if (session.UserId.HasValue && session.UserId.Value != Guid.Empty)
        {
            var recentMessagesDto = session.Messages
                .TakeLast(10)
                .Select(m => new ChatMessageDto(m.Id, m.Role, m.Content, m.CreatedAt))
                .ToList();

            var userMsgCount = session.Messages.Count(m => m.Role == MessageRole.User);

            var job = new MemoryExtractionJob(
                SessionId: session.Id,
                CharacterId: character.Id,
                UserId: session.UserId.Value,
                RecentMessages: recentMessagesDto,
                UserMessageCount: userMsgCount
            );

            _extractionTrigger.NotifyMessageSent(job);
        }

        var characterMood = relationship?.CurrentMood ?? (Enum.TryParse<CharacterMood>(character.DefaultMood, true, out var m) ? m : CharacterMood.Neutral);
        var moodIntensity = relationship?.MoodIntensity ?? 20;

        return Result<SendMessageResponse>.Success(new SendMessageResponse(
            UserMessage: new ChatMessageDto(userMsg.Id, userMsg.Role, userMsg.Content, userMsg.CreatedAt),
            AssistantMessage: new ChatMessageDto(assistantMsg.Id, assistantMsg.Role, assistantMsg.Content, assistantMsg.CreatedAt),
            AffectionScore: newScore,
            RelationshipLevel: newLevel,
            RelationshipStage: stageName,
            CurrentMood: characterMood.ToString(),
            MoodIntensity: moodIntensity,
            AffectionDelta: actualDelta,
            LevelUp: isLevelUp,
            UnlockedEvent: unlockedEventDto
        ));
    }
}
