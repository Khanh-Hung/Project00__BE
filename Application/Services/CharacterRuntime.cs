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
    private readonly ISceneStateTrackerService? _sceneStateTracker;
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
        ILogger<CharacterRuntime> logger,
        ISceneStateTrackerService? sceneStateTracker = null)
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
        _sceneStateTracker = sceneStateTracker;
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

    public async IAsyncEnumerable<CharacterStreamEvent> ProcessTurnStreamAsync(
        CharacterTurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var turnId = request.TurnId ?? Guid.NewGuid();
        var turnLock = InFlightTurnLocks.GetOrAdd(turnId, _ => new SemaphoreSlim(1, 1));

        await turnLock.WaitAsync(ct);
        try
        {
            await foreach (var streamEvent in ExecuteTurnStreamPipelineAsync(turnId, request, ct))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            turnLock.Release();
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

        var (effectiveSceneState, effectiveTransientState, visualSnapshot) = await BuildTurnVisualStateAndSnapshotAsync(
            character,
            session,
            request.UserMessage,
            aiTurn.Reply,
            aiTurn.Mood,
            turnId,
            ct);

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

        // 5. Enqueue Durable Outbox Messages for Side Effects
        var outboxRepo = _unitOfWork.GetRepository<OutboxMessage>();

        // Outbox Job 1: Long-term Memory Extraction
        if (request.UserId != Guid.Empty)
        {
            var recentMessagesDto = session.Messages
                .TakeLast(10)
                .Select(m => new ChatMessageDto(m.Id, m.Role, m.Content, m.CreatedAt))
                .ToList();

            var memoryPayload = new MemoryExtractionOutboxPayload(
                SessionId: session.Id,
                CharacterId: character.Id,
                UserId: request.UserId,
                RecentMessages: recentMessagesDto,
                UserMessageCount: session.Messages.Count(m => m.Role == MessageRole.User)
            );
            var memoryOutbox = new OutboxMessage(
                eventType: OutboxEventTypes.MemoryExtraction,
                payloadJson: JsonSerializer.Serialize(memoryPayload)
            );
            await outboxRepo.AddAsync(memoryOutbox, ct);
        }

        // Outbox Job 2: Voice Generation (if requested)
        if (request.Options?.GenerateVoice == true && character.VoiceProfile != null)
        {
            var voicePayload = new VoiceGenerationOutboxPayload(
                TurnId: turnId,
                CharacterId: character.Id,
                UserId: request.UserId,
                VoiceProfile: character.VoiceProfile,
                Mood: currentMood,
                MoodIntensity: currentIntensity,
                AffectionScore: relationship?.AffectionScore ?? 0,
                RelationshipStage: currentStageName,
                RawText: aiTurn.Reply
            );
            var voiceOutbox = new OutboxMessage(
                eventType: OutboxEventTypes.VoiceGeneration,
                payloadJson: JsonSerializer.Serialize(voicePayload)
            );
            await outboxRepo.AddAsync(voiceOutbox, ct);
        }

        // Outbox Job 3: Scene Image Generation (if requested)
        if (request.Options?.GenerateImage == true && character.VisualIdentity != null)
        {
            var scenePayload = new SceneImageGenerationOutboxPayload(
                TurnId: turnId,
                CharacterId: character.Id,
                UserId: request.UserId,
                Snapshot: visualSnapshot
            );
            var sceneOutbox = new OutboxMessage(
                eventType: OutboxEventTypes.SceneImageGeneration,
                payloadJson: JsonSerializer.Serialize(scenePayload)
            );
            await outboxRepo.AddAsync(sceneOutbox, ct);
        }

        // 6. Truly Atomic Single SaveChanges (Session + Messages + Relationship + CharacterTurn + OutboxMessages)
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

        if (aiTurn.HasWalkedOut)
        {
            session.WalkOut(aiTurn.WalkOutReason ?? "Nhân vật đã rời khỏi cuộc trò chuyện do bị xúc phạm hoặc vi phạm ranh giới.", Clock.Now);
        }

        traceStopwatch.Stop();
        _logger.LogInformation("Turn {TurnId} committed atomically with Outbox in {TotalMs}ms (LLM: {LlmMs}ms). AffectionDelta: {Delta}, Mood: {Mood}, WalkedOut: {WalkedOut}",
            turnId, traceStopwatch.ElapsedMilliseconds, llmStopwatch.ElapsedMilliseconds, appliedDelta, currentMood, aiTurn.HasWalkedOut);

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
            AffectionDelta: appliedDelta,
            HasWalkedOut: aiTurn.HasWalkedOut,
            WalkOutReason: aiTurn.WalkOutReason,
            SessionStatus: session.Status
        );
    }

    private async IAsyncEnumerable<CharacterStreamEvent> ExecuteTurnStreamPipelineAsync(
        Guid turnId,
        CharacterTurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var turnRepo = _unitOfWork.GetRepository<CharacterTurn>();

        // 1. Persistent Database-Backed Idempotency Check: Return full deterministic stream on retry
        var existingTurn = await turnRepo.GetAsync(t => t.TurnId == turnId, ct);
        if (existingTurn != null)
        {
            _logger.LogInformation("Persistent idempotency stream hit for TurnId '{TurnId}'. Returning previous response.", turnId);
            var res = MapExistingTurnToResult(existingTurn);
            yield return CharacterStreamEvent.Token(res.Reply);
            yield return CharacterStreamEvent.Metadata(
                res.Mood,
                res.MoodIntensity,
                res.AffectionDelta,
                res.Relationship.AffectionScore,
                res.Relationship.RelationshipStage ?? "Stranger",
                res.Relationship.CharacterId,
                res.Relationship.UserId
            );
            foreach (var evt in res.Relationship.Events)
            {
                yield return CharacterStreamEvent.EventUnlocked(evt.EventKey, evt.Context);
            }
            yield return CharacterStreamEvent.Done(
                res.TurnId,
                res.MessageId,
                res.Reply,
                res.Relationship,
                res.ActiveMemories
            );
            yield break;
        }

        // 2. Build Context via Context Engine
        var context = await _contextEngine.BuildContextAsync(request.SessionId, request.UserMessage, request.UserId, ct);
        var session = context.Session;
        var character = context.Character;
        var relationship = context.Relationship;

        if (request.CharacterId != Guid.Empty && request.CharacterId != character.Id)
        {
            yield return CharacterStreamEvent.Error(400, $"Requested CharacterId '{request.CharacterId}' does not match Session character '{character.Id}'.");
            yield break;
        }

        // 3. Stream LLM Tokens in Realtime via Incremental Reply Extractor
        var replyExtractor = new IncrementalJsonReplyExtractor();

        await foreach (var chunk in _llmService.GenerateRoleplayTurnStreamAsync(context, ct))
        {
            foreach (var token in replyExtractor.PushChunk(chunk))
            {
                if (!string.IsNullOrEmpty(token))
                {
                    yield return CharacterStreamEvent.Token(token);
                }
            }
        }

        var rawFullText = replyExtractor.GetFullRawAccumulatedText().Trim();

        // 4. Parse Structured Roleplay Turn from Accumulated Text
        var aiTurn = ParseAiTurn(rawFullText);

        // 5. Critical Path: Prepare Session, Relationship & Persistent Turn Record
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

        // Prepare persistent idempotency record
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

        // 6. Enqueue Outbox Messages for Side Effects
        var outboxRepo = _unitOfWork.GetRepository<OutboxMessage>();
        if (request.UserId != Guid.Empty)
        {
            var recentMessagesDto = session.Messages
                .TakeLast(10)
                .Select(m => new ChatMessageDto(m.Id, m.Role, m.Content, m.CreatedAt))
                .ToList();

            var memoryPayload = new MemoryExtractionOutboxPayload(
                SessionId: session.Id,
                CharacterId: character.Id,
                UserId: request.UserId,
                RecentMessages: recentMessagesDto,
                UserMessageCount: session.Messages.Count(m => m.Role == MessageRole.User)
            );
            await outboxRepo.AddAsync(new OutboxMessage(OutboxEventTypes.MemoryExtraction, JsonSerializer.Serialize(memoryPayload)), ct);
        }

        if (request.Options?.GenerateVoice == true && character.VoiceProfile != null)
        {
            var voicePayload = new VoiceGenerationOutboxPayload(
                TurnId: turnId,
                CharacterId: character.Id,
                UserId: request.UserId,
                VoiceProfile: character.VoiceProfile,
                Mood: currentMood,
                MoodIntensity: currentIntensity,
                AffectionScore: relationship?.AffectionScore ?? 0,
                RelationshipStage: currentStageName,
                RawText: aiTurn.Reply
            );
            await outboxRepo.AddAsync(new OutboxMessage(OutboxEventTypes.VoiceGeneration, JsonSerializer.Serialize(voicePayload)), ct);
        }

        var (effectiveStreamSceneState, effectiveStreamTransientState, streamVisualSnapshot) = await BuildTurnVisualStateAndSnapshotAsync(
            character,
            session,
            request.UserMessage,
            aiTurn.Reply,
            currentMood,
            turnId,
            ct);

        if (request.Options?.GenerateImage == true && character.VisualIdentity != null)
        {
            var scenePayload = new SceneImageGenerationOutboxPayload(
                TurnId: turnId,
                CharacterId: character.Id,
                UserId: request.UserId,
                Snapshot: streamVisualSnapshot
            );
            await outboxRepo.AddAsync(new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(scenePayload)), ct);
        }

        // 7. Atomic Commit
        CharacterStreamEvent? concurrencyError = null;
        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Optimistic concurrency conflict on streaming turn {TurnId}", turnId);
            concurrencyError = CharacterStreamEvent.Error(409, "A concurrent update conflicted on the character relationship. Please retry with the same TurnId.");
        }

        if (concurrencyError != null)
        {
            yield return concurrencyError;
            yield break;
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

        if (aiTurn.HasWalkedOut)
        {
            session.WalkOut(aiTurn.WalkOutReason ?? "Nhân vật đã rời khỏi cuộc trò chuyện do bị xúc phạm hoặc vi phạm ranh giới.", Clock.Now);
        }

        // 8. Emit Lifecycle Events
        yield return CharacterStreamEvent.Metadata(
            currentMood.ToString(),
            currentIntensity,
            appliedDelta,
            relationshipDto.AffectionScore,
            currentStageName,
            character.Id,
            request.UserId
        );

        if (aiTurn.Event != null && !string.IsNullOrWhiteSpace(aiTurn.Event.Key))
        {
            yield return CharacterStreamEvent.EventUnlocked(aiTurn.Event.Key, aiTurn.Event.Context);
        }

        yield return CharacterStreamEvent.Done(
            turnId,
            assistantMessage.Id,
            aiTurn.Reply,
            relationshipDto,
            activeMemoriesDto
        );
    }

    private static RoleplayTurnResult ParseAiTurn(string raw)
    {
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(7);
            }
            else if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(3);
            }
            if (cleaned.EndsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - 3);
            }
            cleaned = cleaned.Trim();

            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (root.TryGetProperty("reply", out var replyProp))
            {
                var reply = replyProp.GetString() ?? raw;
                var mood = root.TryGetProperty("mood", out var moodProp) && Enum.TryParse<CharacterMood>(moodProp.GetString(), true, out var pm) ? pm : CharacterMood.Neutral;
                var intensity = root.TryGetProperty("moodIntensity", out var intProp) ? intProp.GetInt32() : 50;
                var delta = root.TryGetProperty("affectionDelta", out var delProp) ? delProp.GetInt32() : 0;

                RelationshipEventProposal? proposal = null;
                if (root.TryGetProperty("event", out var evtProp) && evtProp.TryGetProperty("key", out var kProp) && !string.IsNullOrWhiteSpace(kProp.GetString()))
                {
                    var ctx = evtProp.TryGetProperty("context", out var cProp) ? cProp.GetString() ?? string.Empty : string.Empty;
                    proposal = new RelationshipEventProposal(kProp.GetString()!, ctx);
                }

                var hasWalkedOut = root.TryGetProperty("hasWalkedOut", out var woProp) && (woProp.ValueKind == JsonValueKind.True || (woProp.ValueKind == JsonValueKind.String && bool.TryParse(woProp.GetString(), out var bw) && bw));
                var walkOutReason = root.TryGetProperty("walkOutReason", out var wrProp) && wrProp.ValueKind == JsonValueKind.String ? wrProp.GetString() : null;

                return new RoleplayTurnResult(reply, mood, intensity, delta, proposal, hasWalkedOut, walkOutReason);
            }
        }
        catch
        {
            // Fallback to plain text reply
        }

        return new RoleplayTurnResult(raw, CharacterMood.Neutral, 50, 0, null);
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

    private async Task<(SessionSceneState SceneState, TransientVisualState TransientState, VisualSnapshot Snapshot)> BuildTurnVisualStateAndSnapshotAsync(
        Character character,
        ChatSession session,
        string userMessage,
        string assistantReply,
        CharacterMood currentMood,
        Guid turnId,
        CancellationToken ct)
    {
        var oldState = session.SceneState ?? new SessionSceneState(
            CurrentLocation: character.WorldDescription ?? character.Title ?? "Sanctuary",
            CurrentPosition: "Central Area",
            CurrentOutfit: character.VisualIdentity?.ClothingStyle ?? "Canonical Attire",
            CurrentTimeOfDay: "Daytime",
            HeldItems: null,
            Atmosphere: "Peaceful",
            SceneRevision: 0,
            LastSceneImageUrl: null,
            LastUpdatedAt: Clock.Now
        );

        int targetRevision = (session.SceneState?.SceneRevision ?? 0) + 1;
        SceneStateDelta delta = new SceneStateDelta();

        if (_sceneStateTracker != null)
        {
            try
            {
                delta = await _sceneStateTracker.TrackAndExtractDeltaAsync(
                    character,
                    session.SceneState,
                    userMessage,
                    assistantReply,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to track dynamic scene state during turn {TurnId}.", turnId);
            }
        }

        var updatedSceneState = oldState.ApplyDelta(delta, explicitRevision: targetRevision);
        session.UpdateSceneState(updatedSceneState);

        var transientState = TransientVisualState.FromDelta(
            delta,
            defaultPose: "Graceful posture",
            defaultExpression: currentMood.ToString()
        );

        // Immediate predecessor continuity: previous image is resolved from Revision N - 1
        string? previousSceneImageUrl = (session.SceneState != null && oldState.SceneRevision == targetRevision - 1)
            ? oldState.LastSceneImageUrl
            : null;

        var snapshot = VisualSnapshot.Create(
            turnId: turnId,
            sessionId: session.Id,
            characterId: character.Id,
            sceneRevision: targetRevision,
            visualIdentity: character.VisualIdentity,
            characterAvatarUrl: character.AvatarUrl,
            sceneState: updatedSceneState,
            transientState: transientState,
            previousSceneImageUrl: previousSceneImageUrl
        );

        return (updatedSceneState, transientState, snapshot);
    }
}
