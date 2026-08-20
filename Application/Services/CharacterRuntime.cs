using System.Diagnostics;
using Application.Abstractions.Data;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
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
        var traceStopwatch = Stopwatch.StartNew();

        // 1. Persistent Database-Backed Idempotency Check: Return existing turn on retry
        var turnRepo = _unitOfWork.GetRepository<CharacterTurn>();
        var existingTurn = await turnRepo.GetAsync(t => t.TurnId == turnId, ct);
        if (existingTurn != null)
        {
            _logger.LogInformation("Persistent idempotency hit for TurnId '{TurnId}'. Returning previous response from database without re-executing LLM.", turnId);
            
            var (relLevel, stageName, _) = RelationshipStageResolver.Resolve(existingTurn.AffectionScore);
            var cachedRelationshipDto = new CharacterRelationshipDto(
                Id: Guid.Empty,
                CharacterId: existingTurn.CharacterId,
                UserId: existingTurn.UserId,
                AffectionScore: existingTurn.AffectionScore,
                CurrentMood: Enum.TryParse<CharacterMood>(existingTurn.Mood, out var parsedCachedMood) ? parsedCachedMood : CharacterMood.Neutral,
                MoodIntensity: existingTurn.MoodIntensity,
                Events: new List<RelationshipEventDto>(),
                LastInteractedAt: existingTurn.CreatedAt
            );

            return new CharacterTurnResult(
                MessageId: existingTurn.AssistantMessageId,
                TurnId: existingTurn.TurnId,
                Reply: existingTurn.AssistantReply,
                Relationship: cachedRelationshipDto,
                ActiveMemories: new List<CharacterMemoryDto>(),
                Mood: existingTurn.Mood,
                MoodIntensity: existingTurn.MoodIntensity,
                AffectionDelta: existingTurn.AffectionDelta
            );
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

        // 4. Critical Path: Mutate Session, Relationship & Persistent Turn Record atomically in DB
        var userMsg = session.AddUserMessage(request.UserMessage);
        var assistantMessage = session.AddAssistantMessage(aiTurn.Reply);

        var relationshipRepo = _unitOfWork.GetRepository<CharacterRelationship>();
        if (relationship == null && request.UserId != Guid.Empty)
        {
            relationship = CharacterRelationship.Create(character.Id, request.UserId, character.DefaultAffectionScore);
            await relationshipRepo.AddAsync(relationship, ct);
        }

        int appliedDelta = 0;
        CharacterMood currentMood = aiTurn.Mood;
        int currentIntensity = Math.Clamp(aiTurn.MoodIntensity, 0, 100);

        if (relationship != null)
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

        var (level, currentStageName, _) = RelationshipStageResolver.Resolve(relationship?.AffectionScore ?? character.DefaultAffectionScore, character.CustomMilestonesJson);

        // Commit DB transaction for messages and relationship state
        await _unitOfWork.SaveChangesAsync(ct);

        // Create persistent idempotency record with verified message IDs
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
            relationshipStage: currentStageName
        );
        await turnRepo.AddAsync(turnRecord, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var relationshipDto = new CharacterRelationshipDto(
            Id: relationship?.Id ?? Guid.Empty,
            CharacterId: character.Id,
            UserId: request.UserId,
            AffectionScore: relationship?.AffectionScore ?? character.DefaultAffectionScore,
            CurrentMood: currentMood,
            MoodIntensity: currentIntensity,
            Events: relationship?.Events.Select(e => new RelationshipEventDto(e.EventKey, e.Context, e.UnlockedAt)).ToList() ?? new List<RelationshipEventDto>(),
            LastInteractedAt: relationship?.LastInteractedAt ?? Clock.Now
        );

        var activeMemoriesDto = context.Memories.Select(m => new CharacterMemoryDto(
            Id: m.Id,
            Content: m.Content,
            Type: m.Type,
            Importance: m.Importance,
            Confidence: 1.0m,
            CreatedAt: m.CreatedAt
        )).ToList();

        // 5. Asynchronous & Failure-Isolated Side Effects (Triggered post-commit, non-blocking)
        
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
        _logger.LogInformation("Turn {TurnId} committed in {TotalMs}ms (LLM: {LlmMs}ms). AffectionDelta: {Delta}, Mood: {Mood}",
            turnId, traceStopwatch.ElapsedMilliseconds, llmStopwatch.ElapsedMilliseconds, appliedDelta, currentMood);

        return new CharacterTurnResult(
            MessageId: assistantMessage.Id,
            TurnId: turnId,
            Reply: aiTurn.Reply,
            Relationship: relationshipDto,
            ActiveMemories: activeMemoriesDto,
            AudioUrl: null, // Audio and Image are generated asynchronously in background
            ImageUrl: null,
            Mood: currentMood.ToString(),
            MoodIntensity: currentIntensity,
            AffectionDelta: appliedDelta
        );
    }
}
