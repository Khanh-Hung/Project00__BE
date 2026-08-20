using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Application.Abstractions.Data;
using Application.Common;
using Application.Common.Exceptions;
using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class CharacterRuntime : ICharacterRuntime
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRoleplayContextEngine _contextEngine;
    private readonly ILLMService _llmService;
    private readonly IMemoryExtractionTrigger _extractionTrigger;
    private readonly IVoicePromptCompiler _voiceCompiler;
    private readonly IVoiceGenerationService _voiceService;
    private readonly IVisualPromptCompiler _visualCompiler;
    private readonly IImageGenerationService _imageService;
    private readonly ILogger<CharacterRuntime> _logger;

    // Concurrent in-flight gates to eliminate duplicate LLM execution on concurrent identical TurnIds
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> InFlightTurnLocks = new();

    public CharacterRuntime(
        IUnitOfWork unitOfWork,
        IRoleplayContextEngine contextEngine,
        ILLMService llmService,
        IMemoryExtractionTrigger extractionTrigger,
        IVoicePromptCompiler voiceCompiler,
        IVoiceGenerationService voiceService,
        IVisualPromptCompiler visualCompiler,
        IImageGenerationService imageService,
        ILogger<CharacterRuntime> logger)
    {
        _unitOfWork = unitOfWork;
        _contextEngine = contextEngine;
        _llmService = llmService;
        _extractionTrigger = extractionTrigger;
        _voiceCompiler = voiceCompiler;
        _voiceService = voiceService;
        _visualCompiler = visualCompiler;
        _imageService = imageService;
        _logger = logger;
    }

    public async Task<CharacterTurnResult> ProcessTurnAsync(CharacterTurnRequest request, CancellationToken ct = default)
    {
        var turnId = request.TurnId ?? Guid.NewGuid();
        var turnLock = InFlightTurnLocks.GetOrAdd(turnId, _ => new SemaphoreSlim(1, 1));

        await turnLock.WaitAsync(ct);
        try
        {
            return await ExecuteTurnPipelineAsync(turnId, request, ct);
        }
        finally
        {
            turnLock.Release();
            // Clean up completed turn lock if no other callers are waiting
            if (turnLock.CurrentCount == 1)
            {
                InFlightTurnLocks.TryRemove(turnId, out _);
            }
        }
    }

    private async Task<CharacterTurnResult> ExecuteTurnPipelineAsync(Guid turnId, CharacterTurnRequest request, CancellationToken ct)
    {
        var traceStopwatch = Stopwatch.StartNew();
        var turnRepo = _unitOfWork.GetRepository<CharacterTurn>();

        // 1. Persistent Database-Backed Idempotency Check: Return full deterministic response on retry
        var existingTurn = await turnRepo.GetAsync(t => t.TurnId == turnId, ct);
        if (existingTurn != null)
        {
            _logger.LogInformation("Persistent idempotency hit for TurnId '{TurnId}'. Returning previous response from database without re-executing LLM.", turnId);
            return MapExistingTurnToResult(existingTurn);
        }

        _logger.LogInformation("Starting Character Turn {TurnId} for User {UserId}, Session {SessionId}",
            turnId, request.UserId, request.SessionId);

        // 2. Build Context via Context Engine (Enforces session ownership, item budgets, and isolation)
        var context = await _contextEngine.BuildContextAsync(request.SessionId, request.UserMessage, request.UserId, ct);
        var session = context.Session;
        var character = context.Character;
        var relationship = context.Relationship;

        // Ensure CharacterId consistency if specified by caller
        if (request.CharacterId != Guid.Empty && request.CharacterId != character.Id)
        {
            throw new ArgumentException($"Requested CharacterId '{request.CharacterId}' does not match Session character '{character.Id}'.", nameof(request.CharacterId));
        }

        // 3. Single-Turn LLM Generation
        var llmStopwatch = Stopwatch.StartNew();
        var aiTurn = await _llmService.GenerateRoleplayTurnAsync(context, ct);
        llmStopwatch.Stop();

        // 4. Critical Path: Prepare Session, Relationship & Persistent Turn Record
        var userMsg = session.AddUserMessage(request.UserMessage);
        var assistantMessage = session.AddAssistantMessage(aiTurn.Reply);

        var messageRepo = _unitOfWork.GetRepository<ChatMessage>();
        await messageRepo.AddAsync(userMsg, ct);
        await messageRepo.AddAsync(assistantMessage, ct);

        var relationshipRepo = _unitOfWork.GetRepository<CharacterRelationship>();
        int appliedDelta = 0;
        CharacterMood currentMood = aiTurn.Mood;
        int currentIntensity = Math.Clamp(aiTurn.MoodIntensity, 0, 100);

        if (relationship == null && request.UserId != Guid.Empty)
        {
            var clampedDelta = Math.Clamp(aiTurn.AffectionDelta, -5, 5);
            var initialScore = Math.Clamp(character.DefaultAffectionScore + clampedDelta, -100, 100);
            appliedDelta = initialScore - character.DefaultAffectionScore;

            relationship = CharacterRelationship.Create(
                character.Id,
                request.UserId,
                initialAffection: initialScore,
                initialMood: currentMood,
                initialMoodIntensity: currentIntensity,
                initialTimestamp: Clock.Now);

            if (aiTurn.Event != null && !string.IsNullOrWhiteSpace(aiTurn.Event.Key))
            {
                relationship.TryUnlockEvent(aiTurn.Event.Key, aiTurn.Event.Context, Clock.Now);
            }

            await relationshipRepo.AddAsync(relationship, ct);
        }
        else if (relationship != null)
        {
            var clampedDelta = Math.Clamp(aiTurn.AffectionDelta, -5, 5);
            var (_, _, delta) = relationship.ApplyAffectionDelta(clampedDelta, Clock.Now);
            appliedDelta = delta;

            relationship.UpdateMood(currentMood, currentIntensity, Clock.Now);

            if (aiTurn.Event != null && !string.IsNullOrWhiteSpace(aiTurn.Event.Key))
            {
                relationship.TryUnlockEvent(aiTurn.Event.Key, aiTurn.Event.Context, Clock.Now);
            }
        }

        var (level, currentStageName, _) = RelationshipStageResolver.Resolve(
            relationship?.AffectionScore ?? character.DefaultAffectionScore,
            character.CustomMilestonesJson);

        var eventsDto = relationship?.Events
            .Select(e => new RelationshipEventDto(e.EventKey, e.Context, e.UnlockedAt))
            .ToList() ?? new List<RelationshipEventDto>();

        var activeMemoriesDto = context.Memories
            .Select(m => new CharacterMemoryDto(m.Id, m.Content, m.Type, m.Importance, 1.0m, m.CreatedAt))
            .ToList();

        var eventsJson = JsonSerializer.Serialize(eventsDto);
        var memoriesJson = JsonSerializer.Serialize(activeMemoriesDto);

        // Prepare persistent idempotency record with verified message IDs and full deterministic snapshot
        var turnRecord = new CharacterTurn(
            turnId: turnId,
            sessionId: session.Id,
            userId: request.UserId,
            characterId: character.Id,
            userMessageId: userMsg.Id,
            assistantMessageId: assistantMessage.Id,
            userMessage: request.UserMessage,
            assistantReply: aiTurn.Reply,
            mood: currentMood.ToString(),
            moodIntensity: currentIntensity,
            affectionDelta: appliedDelta,
            affectionScore: relationship?.AffectionScore ?? character.DefaultAffectionScore,
            relationshipStage: currentStageName,
            relationshipId: relationship?.Id ?? Guid.Empty,
            lastInteractedAt: relationship?.LastInteractedAt ?? Clock.Now,
            eventsJson: eventsJson,
            activeMemoriesJson: memoriesJson
        );
        await turnRepo.AddAsync(turnRecord, ct);

        // 5. Truly Atomic Single SaveChanges
        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Optimistic concurrency conflict on CharacterRelationship for Turn {TurnId}.", turnId);
            throw new CharacterTurnConcurrencyException(turnId, character.Id, request.UserId, "A concurrent update conflicted on the character relationship. Please retry with the same TurnId.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_CharacterTurns_TurnId") == true || ex.Message.Contains("IX_CharacterTurns_TurnId"))
        {
            _logger.LogInformation("Concurrent duplicate TurnId detected on DB insert for Turn {TurnId}. Fetching committed record.", turnId);
            var committedTurn = await turnRepo.GetAsync(t => t.TurnId == turnId, ct);
            if (committedTurn != null)
            {
                return MapExistingTurnToResult(committedTurn);
            }
            throw;
        }

        var relationshipDto = new CharacterRelationshipDto(
            Id: relationship?.Id ?? Guid.Empty,
            CharacterId: character.Id,
            UserId: request.UserId,
            AffectionScore: relationship?.AffectionScore ?? character.DefaultAffectionScore,
            CurrentMood: currentMood,
            MoodIntensity: currentIntensity,
            Events: eventsDto,
            LastInteractedAt: relationship?.LastInteractedAt ?? Clock.Now,
            RelationshipStage: currentStageName
        );

        // 6. Asynchronous & Failure-Isolated Side Effects (Dispatched post-commit, non-blocking)
        
        // Side effect 1: Long-term memory extraction trigger (Background Worker)
        try
        {
            if (request.UserId != Guid.Empty)
            {
                var recentMessagesDto = session.Messages
                    .TakeLast(10)
                    .Select(m => new ChatMessageDto(m.Id, m.Role, m.Content, m.CreatedAt))
                    .ToList();

                var extractionJob = new MemoryExtractionJob(
                    SessionId: session.Id,
                    CharacterId: character.Id,
                    UserId: request.UserId,
                    RecentMessages: recentMessagesDto,
                    UserMessageCount: session.Messages.Count(m => m.Role == MessageRole.User)
                );
                _extractionTrigger.NotifyMessageSent(extractionJob);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory extraction trigger failed for Turn {TurnId}. Chat completed successfully.", turnId);
        }

        // Side effect 2: Optional Non-blocking Background Voice Audio Generation
        if (request.Options?.GenerateVoice == true && character.VoiceProfile != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var voiceContext = new VoiceContext(
                        Voice: character.VoiceProfile,
                        Mood: relationship?.CurrentMood ?? CharacterMood.Neutral,
                        MoodIntensity: currentIntensity,
                        AffectionScore: relationship?.AffectionScore ?? 0,
                        RelationshipStage: currentStageName,
                        RawText: aiTurn.Reply
                    );
                    var voiceReq = _voiceCompiler.CompileVoiceRequest(voiceContext);
                    await _voiceService.GenerateVoiceAsync(voiceReq, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background voice generation failed for Turn {TurnId}", turnId);
                }
            });
        }

        // Side effect 3: Optional Non-blocking Background Scene Image Generation
        if (request.Options?.GenerateImage == true && character.VisualIdentity != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var scene = new SceneContext(
                        Location: character.Title,
                        Expression: currentMood.ToString()
                    );
                    var prompt = _visualCompiler.CompileScenePrompt(character, scene, relationship);
                    await _imageService.GenerateImageAsync(new ImageGenerationRequest(prompt), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background scene image generation failed for Turn {TurnId}", turnId);
                }
            });
        }

        traceStopwatch.Stop();
        _logger.LogInformation("Turn {TurnId} committed atomically in {TotalMs}ms (LLM: {LlmMs}ms). AffectionDelta: {Delta}, Mood: {Mood}",
            turnId, traceStopwatch.ElapsedMilliseconds, llmStopwatch.ElapsedMilliseconds, appliedDelta, currentMood);

        return new CharacterTurnResult(
            MessageId: assistantMessage.Id,
            TurnId: turnId,
            Reply: aiTurn.Reply,
            Relationship: relationshipDto,
            ActiveMemories: activeMemoriesDto,
            AudioUrl: null,
            ImageUrl: null,
            Mood: currentMood.ToString(),
            MoodIntensity: currentIntensity,
            AffectionDelta: appliedDelta
        );
    }

    private static CharacterTurnResult MapExistingTurnToResult(CharacterTurn existingTurn)
    {
        var restoredEvents = string.IsNullOrWhiteSpace(existingTurn.EventsJson)
            ? new List<RelationshipEventDto>()
            : JsonSerializer.Deserialize<List<RelationshipEventDto>>(existingTurn.EventsJson) ?? new List<RelationshipEventDto>();

        var restoredMemories = string.IsNullOrWhiteSpace(existingTurn.ActiveMemoriesJson)
            ? new List<CharacterMemoryDto>()
            : JsonSerializer.Deserialize<List<CharacterMemoryDto>>(existingTurn.ActiveMemoriesJson) ?? new List<CharacterMemoryDto>();

        var cachedRelationshipDto = new CharacterRelationshipDto(
            Id: existingTurn.RelationshipId,
            CharacterId: existingTurn.CharacterId,
            UserId: existingTurn.UserId,
            AffectionScore: existingTurn.AffectionScore,
            CurrentMood: Enum.TryParse<CharacterMood>(existingTurn.Mood, out var parsedCachedMood) ? parsedCachedMood : CharacterMood.Neutral,
            MoodIntensity: existingTurn.MoodIntensity,
            Events: restoredEvents,
            LastInteractedAt: existingTurn.LastInteractedAt,
            RelationshipStage: existingTurn.RelationshipStage
        );

        return new CharacterTurnResult(
            MessageId: existingTurn.AssistantMessageId,
            TurnId: existingTurn.TurnId,
            Reply: existingTurn.AssistantReply,
            Relationship: cachedRelationshipDto,
            ActiveMemories: restoredMemories,
            Mood: existingTurn.Mood,
            MoodIntensity: existingTurn.MoodIntensity,
            AffectionDelta: existingTurn.AffectionDelta
        );
    }
}
