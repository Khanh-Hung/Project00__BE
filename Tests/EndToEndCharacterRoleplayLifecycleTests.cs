using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

/// <summary>
/// 10-Turn Orchestration Integration Test Suite.
/// 
/// Scope & Boundaries:
/// - Verifies the end-to-end orchestration contract between:
///   HTTP/Mediator -> CharacterRuntime -> RoleplayContextEngine -> CharacterRelationship -> MemoryService -> LorebookEngine -> LLMService -> VisualSnapshot -> VoiceContext -> Transactional Outbox.
/// - Deterministic mock LLM, voice, and image services are used to ensure $0 test execution cost.
/// - Vector cosine similarity calculation is stubbed via MockMemoryService (tested separately in SemanticMemoryRetrievalTests).
/// - Scene predecessor chaining in Turn 9 is verified against a seeded Revision 8 artifact (full multi-turn real image GPU inference is tested in VisualContinuity8TurnBenchmarkTests).
/// </summary>
public sealed class EndToEndCharacterRoleplayLifecycleTests
{
    private sealed class FakeCurrentUserProvider : ICurrentUserProvider
    {
        public string? CurrentUserId { get; set; }
        public string? CurrentUserName => "Alex";
        public string? CurrentUserEmail => "alex@project00.ai";
        public string? CurrentUserRole => "User";
        public FakeCurrentUserProvider(string? userId = null) => CurrentUserId = userId;
    }

    private sealed class ScriptedRoleplayLLMService : ILLMService
    {
        private readonly Queue<RoleplayTurnResult> _scriptedResponses = new();

        public void EnqueueResponse(
            string reply,
            CharacterMood mood = CharacterMood.Neutral,
            int moodIntensity = 50,
            int affectionDelta = 0,
            RelationshipEventProposal? relEvent = null,
            bool hasWalkedOut = false,
            string? walkOutReason = null)
        {
            _scriptedResponses.Enqueue(new RoleplayTurnResult(
                Reply: reply,
                Mood: mood,
                MoodIntensity: moodIntensity,
                AffectionDelta: affectionDelta,
                Event: relEvent,
                HasWalkedOut: hasWalkedOut,
                WalkOutReason: walkOutReason
            ));
        }

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(RoleplayContext context, CancellationToken ct = default)
        {
            if (_scriptedResponses.Count == 0)
            {
                return Task.FromResult(new RoleplayTurnResult("Default scripted response.", CharacterMood.Neutral, 50, 0));
            }
            return Task.FromResult(_scriptedResponses.Dequeue());
        }

        public async IAsyncEnumerable<string> GenerateRoleplayTurnStreamAsync(
            RoleplayContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = await GenerateRoleplayTurnAsync(context, ct);
            yield return response.Reply;
        }

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, IReadOnlyCollection<CharacterMemory>? memories = null, CancellationToken ct = default)
            => GenerateRoleplayTurnAsync(null!, ct);

        public Task<string> GenerateRoleplayResponseAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, CancellationToken ct = default)
            => Task.FromResult("Default response");

        public Task<GeneratedCharacterDto> GenerateCharacterProfileAsync(string idea, string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRandomIdeasAsync(int count = 4, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRoleplaySuggestionsAsync(Character character, IReadOnlyCollection<ChatMessage> history, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateSceneImageAsync(GenerateSceneImageRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MemoryCandidate>> ExtractMemoryCandidatesAsync(Character character, IReadOnlyCollection<ChatMessageDto> recentMessages, CancellationToken ct = default) => Task.FromResult(new List<MemoryCandidate>());
        public Task<ProactiveAiReachoutResult> GenerateProactiveReachoutAsync(Character character, UserProfile userProfile, CancellationToken ct = default) => Task.FromResult(new ProactiveAiReachoutResult("Hi", "Matched"));
    }

    private sealed class ScriptedSceneStateTracker : ISceneStateTrackerService
    {
        public Queue<SceneStateDelta> ScriptedDeltas { get; } = new();

        public Task<SessionSceneState> TrackAndExtractStateAsync(
            Character character,
            SessionSceneState? currentState,
            string userMessage,
            string assistantMessage,
            CancellationToken ct = default)
        {
            var baseState = currentState ?? new SessionSceneState(
                CurrentLocation: "Royal Archives",
                CurrentOutfit: "White Royal Dress",
                Atmosphere: "Quiet & Formal",
                SceneRevision: 0
            );

            var delta = ScriptedDeltas.Count > 0 ? ScriptedDeltas.Dequeue() : new SceneStateDelta();
            return Task.FromResult(baseState.ApplyDelta(delta));
        }

        public Task<SceneStateDelta> TrackAndExtractDeltaAsync(
            Character character,
            SessionSceneState? currentState,
            string userMessage,
            string assistantMessage,
            CancellationToken ct = default)
        {
            var delta = ScriptedDeltas.Count > 0 ? ScriptedDeltas.Dequeue() : new SceneStateDelta();
            return Task.FromResult(delta);
        }
    }

    private sealed class MockMemoryService : IMemoryService
    {
        public List<CharacterMemory> StoredMemories { get; } = new();

        public Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(
            Guid userId,
            Guid characterId,
            int maxCount = 6,
            string? queryText = null,
            CancellationToken ct = default)
        {
            var filtered = StoredMemories
                .Where(m => m.UserId == userId && m.CharacterId == characterId)
                .Take(maxCount)
                .ToList();
            return Task.FromResult<IReadOnlyList<CharacterMemory>>(filtered);
        }

        public Task<MemoryExtractionMetrics> StoreCandidatesAsync(
            Guid userId,
            Guid characterId,
            Guid? sessionId,
            IEnumerable<MemoryCandidate> candidates,
            CancellationToken ct = default)
        {
            foreach (var c in candidates)
            {
                StoredMemories.Add(CharacterMemory.Create(characterId, userId, c.Content, c.Type, c.Importance, 0.9m, sessionId, "[0.1, 0.2]"));
            }
            return Task.FromResult(new MemoryExtractionMetrics(candidates.Count(), candidates.Count(), 0, 0, 0));
        }
    }

    private sealed class MockLorebookEngine : ILorebookEngine
    {
        private readonly List<LorebookEntry> _entries = new();

        public void AddEntry(LorebookEntry entry) => _entries.Add(entry);

        public Task<IReadOnlyList<LorebookEntry>> MatchLorebookEntriesAsync(
            Guid characterId,
            string userMessage,
            IReadOnlyList<ChatMessage> recentMessages,
            int maxTokenBudget,
            CancellationToken ct = default)
        {
            var matched = _entries.Where(e =>
                e.CharacterId == characterId &&
                e.Keywords.Any(k => userMessage.Contains(k, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            return Task.FromResult<IReadOnlyList<LorebookEntry>>(matched);
        }
    }

    private sealed class MockMemoryExtractionTrigger : IMemoryExtractionTrigger
    {
        public List<MemoryExtractionJob> TriggeredJobs { get; } = new();
        public bool NotifyMessageSent(MemoryExtractionJob job)
        {
            TriggeredJobs.Add(job);
            return true;
        }
    }

    private sealed class MockVoiceService : IVoiceGenerationService
    {
        public int GenerateCount { get; private set; }
        public Task<VoiceGenerationResult> GenerateVoiceAsync(VoiceGenerationRequest request, CancellationToken ct = default)
        {
            GenerateCount++;
            return Task.FromResult(new VoiceGenerationResult("https://cdn.project00.ai/voice_mock.mp3", "audio/mpeg", 2));
        }
    }

    private sealed class MockImageService : IImageGenerationService
    {
        public int GenerateCount { get; private set; }
        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
        {
            GenerateCount++;
            return Task.FromResult($"https://cdn.project00.ai/image_mock_{GenerateCount}.png");
        }

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            GenerateCount++;
            return Task.FromResult($"https://cdn.project00.ai/image_mock_{GenerateCount}.png");
        }
    }

    [Fact]
    public async Task Ten_Turn_Character_Roleplay_Orchestration_Integration_Test()
    {
        // 1. Setup Database & In-Memory Context
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        await using var db = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(db);

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // 2. Character Setup with Custom Milestones (Stranger -> Acquaintance -> Friend -> Soulmate)
        var customMilestones = new List<RelationshipMilestoneDto>
        {
            new("Stranger", -25, 14, "Nhân vật giữ khoảng cách lịch thiệp xã giao, thận trọng quan sát."),
            new("Acquaintance", 15, 34, "Nhân vật cởi mở, bắt đầu chia sẻ thói quen đời thường."),
            new("Friend", 35, 70, "Nhân vật tin tưởng, coi bạn là người bạn đáng tin cậy."),
            new("Soulmate", 71, 100, "Gắn kết tri kỷ sâu sắc.")
        };
        var customMilestonesJson = JsonSerializer.Serialize(customMilestones);

        var visualIdentity = new CharacterVisualIdentity(
            Hair: "Silver, waist-length",
            Eyes: "Luminous Violet",
            Body: "Slender, graceful anime proportions",
            CanonicalReferenceUrl: "https://cdn.project00.ai/aeloria_canon.png"
        );
        var voiceProfile = new CharacterVoiceProfile("en-US-AeloriaNeural", "en-US", "Female", "YoungAdult", "Soft", "Warm", "Normal", "Normal", "Graceful, regal");

        var character = new Character(
            name: "Aeloria",
            title: "Silver Dragon Princess",
            avatarUrl: "https://cdn.project00.ai/aeloria_avatar.png",
            personalityPrompt: "Regal, guarded, intellectually curious, fiercely protective of dragon history",
            greeting: "Who approaches the inner sanctum of the royal archives?",
            category: "Fantasy",
            defaultAffectionScore: 0,
            defaultMood: "Neutral",
            customMilestonesJson: customMilestonesJson,
            visualIdentity: visualIdentity,
            voiceProfile: voiceProfile
        )
        {
            Id = charId
        };
        await db.Characters.AddAsync(character);

        // 3. Lorebook Setup
        var lorebookEngine = new MockLorebookEngine();
        var silverOrderLore = new LorebookEntry(
            characterId: charId,
            title: "The Silver Order",
            content: "The Silver Order is an ancient chivalric order sworn to guard dragon relics.",
            keywords: new List<string> { "order", "silver order", "paladin", "knights" },
            category: LorebookCategory.Faction,
            isConstant: false,
            priority: 100
        );
        lorebookEngine.AddEntry(silverOrderLore);
        await db.LorebookEntries.AddAsync(silverOrderLore);

        // 4. Session Setup
        var session = new ChatSession(charId, userId, "10-Turn Roleplay Benchmark");
        await db.ChatSessions.AddAsync(session);
        await db.SaveChangesAsync();

        // 5. Wire Services
        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var mockMemoryService = new MockMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, mockMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance, lorebookEngine);
        var scriptedLLM = new ScriptedRoleplayLLMService();
        var mockTrigger = new MockMemoryExtractionTrigger();
        var voiceCompiler = new VoicePromptCompiler();
        var mockVoiceService = new MockVoiceService();
        var visualCompiler = new VisualPromptCompiler();
        var mockImageService = new MockImageService();
        var sceneTracker = new ScriptedSceneStateTracker();

        var runtime = new CharacterRuntime(
            unitOfWork,
            contextEngine,
            scriptedLLM,
            mockTrigger,
            voiceCompiler,
            mockVoiceService,
            visualCompiler,
            mockImageService,
            NullLogger<CharacterRuntime>.Instance,
            sceneTracker
        );

        // ==========================================
        // TURN 1: Stranger Greeting & Initial Memory
        // ==========================================
        sceneTracker.ScriptedDeltas.Enqueue(new SceneStateDelta(LocationChange: "Royal Archives", OutfitChange: "White Royal Dress", AtmosphereChange: "Quiet & Formal"));
        scriptedLLM.EnqueueResponse(
            reply: "Hmph, a scholar? State your business in the royal archives.",
            mood: CharacterMood.Neutral,
            moodIntensity: 50,
            affectionDelta: 5
        );

        var t1Req = new CharacterTurnRequest(
            userId,
            charId,
            session.Id,
            "Greetings Princess, I am Alex, a traveling scholar seeking ancient lore.",
            Guid.NewGuid(),
            new CharacterTurnOptions(GenerateVoice: true, GenerateImage: true));
        var t1Result = await runtime.ProcessTurnAsync(t1Req);

        Assert.Equal(5, t1Result.Relationship.AffectionScore);
        Assert.Equal("Stranger", t1Result.Relationship.RelationshipStage); // Invariant: score 5 remains Stranger (<15)
        Assert.Equal("Neutral", t1Result.Mood);

        // Verify Outbox queued MemoryExtraction, VoiceGeneration, and SceneImageGeneration (Rev 1)
        var t1Outbox = await db.OutboxMessages.AsNoTracking().Where(m => m.Status == OutboxStatus.Pending).ToListAsync();
        Assert.Contains(t1Outbox, m => m.EventType == OutboxEventTypes.MemoryExtraction);
        Assert.Contains(t1Outbox, m => m.EventType == OutboxEventTypes.VoiceGeneration);
        Assert.Contains(t1Outbox, m => m.EventType == OutboxEventTypes.SceneImageGeneration);

        // Simulate Memory Worker extracting Turn 1 memory to DB
        await mockMemoryService.StoreCandidatesAsync(userId, charId, session.Id, new[]
        {
            new MemoryCandidate("Alex is a traveling scholar researching dragon history", MemoryType.Fact, 5)
        });

        // ==========================================
        // TURN 2: Building Rapport (Affection 5 -> 10, Still Stranger)
        // ==========================================
        scriptedLLM.EnqueueResponse(
            reply: "You... actually know about my ancestors' sacrifices?",
            mood: CharacterMood.Curious,
            moodIntensity: 70,
            affectionDelta: 5
        );

        var t2Req = new CharacterTurnRequest(userId, charId, session.Id, "I have read of your noble dragon lineage and hold deep respect for your house.", Guid.NewGuid());
        var t2Result = await runtime.ProcessTurnAsync(t2Req);

        Assert.Equal(10, t2Result.Relationship.AffectionScore);
        Assert.Equal("Stranger", t2Result.Relationship.RelationshipStage); // Invariant: score 10 remains Stranger (<15)
        Assert.Equal("Curious", t2Result.Mood);

        // ==========================================
        // TURN 3: Gift Giving -> Level Up to Acquaintance (15) + Event
        // ==========================================
        scriptedLLM.EnqueueResponse(
            reply: "A star crystal?! You are far more thoughtful than other mortals who wander here.",
            mood: CharacterMood.Happy,
            moodIntensity: 85,
            affectionDelta: 5,
            relEvent: new RelationshipEventProposal("GIFT_STAR_CRYSTAL", "Alex offered a rare star crystal to the archive")
        );

        var t3Req = new CharacterTurnRequest(userId, charId, session.Id, "I brought an ancient star crystal as an offering to your archive.", Guid.NewGuid());
        var t3Result = await runtime.ProcessTurnAsync(t3Req);

        Assert.Equal(15, t3Result.Relationship.AffectionScore);
        Assert.Equal("Acquaintance", t3Result.Relationship.RelationshipStage); // Invariant: Stage transitions to Acquaintance at threshold 15!
        Assert.Equal("Happy", t3Result.Mood);

        // Verify Relationship Event was persisted
        var rel3 = await db.CharacterRelationships.AsNoTracking().FirstAsync(r => r.UserId == userId && r.CharacterId == charId);
        Assert.Contains(rel3.Events, e => e.EventKey == "GIFT_STAR_CRYSTAL");

        // ==========================================
        // TURN 4: Memory Retrieval Injection
        // ==========================================
        scriptedLLM.EnqueueResponse(
            reply: "I remember your words from when you arrived, scholar Alex. Very well, ask your questions.",
            mood: CharacterMood.Neutral,
            moodIntensity: 50,
            affectionDelta: 4
        );

        var t4Req = new CharacterTurnRequest(userId, charId, session.Id, "As a scholar seeking lost lore, which tome should I consult first?", Guid.NewGuid());
        var t4Result = await runtime.ProcessTurnAsync(t4Req);

        Assert.Equal(19, t4Result.Relationship.AffectionScore);
        Assert.Equal("Acquaintance", t4Result.Relationship.RelationshipStage);

        // Verify that RoleplayContext injected the stored memory
        var t4TurnRecord = await db.CharacterTurns.AsNoTracking().FirstAsync(t => t.TurnId == t4Req.TurnId);
        Assert.Contains("traveling scholar", t4TurnRecord.ActiveMemoriesJson);

        // ==========================================
        // TURN 5: Lorebook Dynamic Activation
        // ==========================================
        scriptedLLM.EnqueueResponse(
            reply: "The Silver Order was founded centuries ago by the first High Paladin.",
            mood: CharacterMood.Neutral,
            moodIntensity: 50,
            affectionDelta: 3
        );

        var t5Req = new CharacterTurnRequest(userId, charId, session.Id, "What can you tell me about the Silver Order knights?", Guid.NewGuid());
        var t5Result = await runtime.ProcessTurnAsync(t5Req);

        Assert.Equal(22, t5Result.Relationship.AffectionScore);
        Assert.Equal("Acquaintance", t5Result.Relationship.RelationshipStage);

        // ==========================================
        // TURN 6: Conflict / Insult -> Negative Delta & Angry Mood
        // ==========================================
        scriptedLLM.EnqueueResponse(
            reply: "Silence! How dare you question the courage of the fallen knights?!",
            mood: CharacterMood.Angry,
            moodIntensity: 90,
            affectionDelta: -5,
            relEvent: new RelationshipEventProposal("INSULT_SILVER_ORDER", "Alex questioned the honor of the Silver Order")
        );

        var t6Req = new CharacterTurnRequest(userId, charId, session.Id, "Perhaps the knights of the Silver Order were cowards who fled during the war.", Guid.NewGuid());
        var t6Result = await runtime.ProcessTurnAsync(t6Req);

        Assert.Equal(17, t6Result.Relationship.AffectionScore); // 22 - 5 = 17
        Assert.Equal("Acquaintance", t6Result.Relationship.RelationshipStage);
        Assert.Equal("Angry", t6Result.Mood);
        Assert.Equal(90, t6Result.MoodIntensity);

        // ==========================================
        // TURN 7: Apology & Mood Recovery
        // ==========================================
        scriptedLLM.EnqueueResponse(
            reply: "...I see. A true scholar should know better, but I accept your apology.",
            mood: CharacterMood.Neutral,
            moodIntensity: 60,
            affectionDelta: 5
        );

        var t7Req = new CharacterTurnRequest(userId, charId, session.Id, "I deeply apologize, Aeloria. I spoke out of ignorance, not disrespect.", Guid.NewGuid());
        var t7Result = await runtime.ProcessTurnAsync(t7Req);

        Assert.Equal(22, t7Result.Relationship.AffectionScore);
        Assert.Equal("Acquaintance", t7Result.Relationship.RelationshipStage);
        Assert.Equal("Neutral", t7Result.Mood);

        // ==========================================
        // TURN 8: Deep Bonding & Stage Transition -> Friend (Threshold 35)
        // ==========================================
        scriptedLLM.EnqueueResponse(
            reply: "Thank you, Alex. I feel I can truly trust you as a genuine friend.",
            mood: CharacterMood.Affectionate,
            moodIntensity: 95,
            affectionDelta: 5,
            relEvent: new RelationshipEventProposal("TRUST_CONFIDANT", "Aeloria accepted Alex as a trusted friend")
        );

        // Manually adjust affection in relationship to 30 prior to turn 8 to test the exact 35 threshold
        var relToBoost = await db.CharacterRelationships.FirstAsync(r => r.UserId == userId && r.CharacterId == charId);
        relToBoost.ApplyAffectionDelta(8, Clock.Now); // 22 + 8 = 30
        await db.SaveChangesAsync();

        var t8Req = new CharacterTurnRequest(userId, charId, session.Id, "I promise to write only the true, untarnished history of your house.", Guid.NewGuid());
        var t8Result = await runtime.ProcessTurnAsync(t8Req);

        Assert.Equal(35, t8Result.Relationship.AffectionScore);
        Assert.Equal("Friend", t8Result.Relationship.RelationshipStage); // Invariant: Stage transitions to Friend at threshold 35!

        // ==========================================
        // TURN 9: Scene Transition -> SceneRevision 9 & Predecessor Link
        // ==========================================
        sceneTracker.ScriptedDeltas.Enqueue(new SceneStateDelta(LocationChange: "Moonlit Garden", OutfitChange: "White Royal Dress", AtmosphereChange: "Moonlit & Serene"));
        scriptedLLM.EnqueueResponse(
            reply: "Yes, let us walk to the moonlit garden. The evening breeze will be pleasant.",
            mood: CharacterMood.Playful,
            moodIntensity: 60,
            affectionDelta: 3
        );

        // Seed SceneImage artifact for Revision 8 to simulate completed Rev 8 predecessor
        var rev8Artifact = new SceneImage(session.Id, charId, Guid.NewGuid(), 8, "https://cdn.project00.ai/scene_rev8.png", "prompt 8");
        await db.SceneImages.AddAsync(rev8Artifact);
        await db.SaveChangesAsync();

        var t9Req = new CharacterTurnRequest(
            userId,
            charId,
            session.Id,
            "Shall we step outside into the garden to continue our discussion?",
            Guid.NewGuid(),
            new CharacterTurnOptions(GenerateVoice: true, GenerateImage: true));
        var t9Result = await runtime.ProcessTurnAsync(t9Req);

        Assert.Equal(38, t9Result.Relationship.AffectionScore);
        Assert.Equal("Friend", t9Result.Relationship.RelationshipStage);

        // Verify Outbox for Revision 9 was queued with Predecessor pointing to Rev 8 image
        var rev9Outbox = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(rev9Outbox);
        var rev9Payload = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(rev9Outbox.PayloadJson);
        Assert.NotNull(rev9Payload?.Snapshot);
        Assert.Equal(9, rev9Payload.Snapshot.SceneRevision);
        Assert.Equal("Moonlit Garden", rev9Payload.Snapshot.SceneState.CurrentLocation);
        Assert.Equal("https://cdn.project00.ai/scene_rev8.png", rev9Payload.Snapshot.PreviousSceneImageUrl);

        // ==========================================
        // TURN 10: Persistent Turn Idempotency Replay
        // ==========================================
        var t10Req = new CharacterTurnRequest(userId, charId, session.Id, "Look at how the starlight shines upon the roses.", Guid.NewGuid());
        scriptedLLM.EnqueueResponse(
            reply: "Indeed, it is a magnificent sight to behold together.",
            mood: CharacterMood.Happy,
            moodIntensity: 80,
            affectionDelta: 2
        );

        // Pass 1: Normal Execution
        var t10Result1 = await runtime.ProcessTurnAsync(t10Req);
        Assert.Equal(40, t10Result1.Relationship.AffectionScore);
        Assert.Equal("Indeed, it is a magnificent sight to behold together.", t10Result1.Reply);
        Assert.Equal("Friend", t10Result1.Relationship.RelationshipStage);

        var totalMessagesBeforeReplay = await db.ChatMessages.AsNoTracking().CountAsync();
        var totalTurnsBeforeReplay = await db.CharacterTurns.AsNoTracking().CountAsync();

        // Pass 2: Replay identical TurnId
        var t10Result2 = await runtime.ProcessTurnAsync(t10Req);

        // Invariant: Exact identical response returned without re-executing LLM or duplicating records
        Assert.Equal(t10Result1.MessageId, t10Result2.MessageId);
        Assert.Equal(t10Result1.Reply, t10Result2.Reply);
        Assert.Equal(t10Result1.Relationship.AffectionScore, t10Result2.Relationship.AffectionScore);
        Assert.Equal(t10Result1.Relationship.RelationshipStage, t10Result2.Relationship.RelationshipStage);

        var totalMessagesAfterReplay = await db.ChatMessages.AsNoTracking().CountAsync();
        var totalTurnsAfterReplay = await db.CharacterTurns.AsNoTracking().CountAsync();

        Assert.Equal(totalMessagesBeforeReplay, totalMessagesAfterReplay);
        Assert.Equal(totalTurnsBeforeReplay, totalTurnsAfterReplay);
    }

    [Fact]
    public async Task Affection_Delta_Is_Strictly_Clamped_Within_Domain_Boundaries()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        await using var db = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(db);

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var character = new Character("TestChar", "Title", "https://avatar.png", "Personality", "Hello", "Fantasy") { Id = charId };
        await db.Characters.AddAsync(character);
        var session = new ChatSession(charId, userId, "Session");
        await db.ChatSessions.AddAsync(session);
        await db.SaveChangesAsync();

        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var mockMemoryService = new MockMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, mockMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var scriptedLLM = new ScriptedRoleplayLLMService();
        var mockTrigger = new MockMemoryExtractionTrigger();
        var voiceCompiler = new VoicePromptCompiler();
        var mockVoiceService = new MockVoiceService();
        var visualCompiler = new VisualPromptCompiler();
        var mockImageService = new MockImageService();
        var sceneTracker = new ScriptedSceneStateTracker();

        var runtime = new CharacterRuntime(
            unitOfWork,
            contextEngine,
            scriptedLLM,
            mockTrigger,
            voiceCompiler,
            mockVoiceService,
            visualCompiler,
            mockImageService,
            NullLogger<CharacterRuntime>.Instance,
            sceneTracker
        );

        // Turn 1: LLM attempts +99 delta -> Runtime strictly clamps to +5
        scriptedLLM.EnqueueResponse("Extreme Praise!", CharacterMood.Happy, 80, affectionDelta: 99);
        var t1 = await runtime.ProcessTurnAsync(new CharacterTurnRequest(userId, charId, session.Id, "Great job!", Guid.NewGuid()));
        Assert.Equal(5, t1.AffectionDelta);
        Assert.Equal(5, t1.Relationship.AffectionScore);

        // Turn 2: LLM attempts -99 delta -> Runtime strictly clamps to -5
        scriptedLLM.EnqueueResponse("Extreme Anger!", CharacterMood.Angry, 90, affectionDelta: -99);
        var t2 = await runtime.ProcessTurnAsync(new CharacterTurnRequest(userId, charId, session.Id, "I hate you!", Guid.NewGuid()));
        Assert.Equal(-5, t2.AffectionDelta);
        Assert.Equal(0, t2.Relationship.AffectionScore);
    }

    [Fact]
    public async Task Relationship_Event_Deduplication_And_Persistence_Contract()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        await using var db = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(db);

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var character = new Character("TestChar", "Title", "https://avatar.png", "Personality", "Hello", "Fantasy") { Id = charId };
        await db.Characters.AddAsync(character);
        var session = new ChatSession(charId, userId, "Session");
        await db.ChatSessions.AddAsync(session);
        await db.SaveChangesAsync();

        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var mockMemoryService = new MockMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, mockMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var scriptedLLM = new ScriptedRoleplayLLMService();
        var mockTrigger = new MockMemoryExtractionTrigger();
        var voiceCompiler = new VoicePromptCompiler();
        var mockVoiceService = new MockVoiceService();
        var visualCompiler = new VisualPromptCompiler();
        var mockImageService = new MockImageService();
        var sceneTracker = new ScriptedSceneStateTracker();

        var runtime = new CharacterRuntime(
            unitOfWork,
            contextEngine,
            scriptedLLM,
            mockTrigger,
            voiceCompiler,
            mockVoiceService,
            visualCompiler,
            mockImageService,
            NullLogger<CharacterRuntime>.Instance,
            sceneTracker
        );

        // Turn 1: Propose Event FIRST_PROMISE
        scriptedLLM.EnqueueResponse("I promise.", CharacterMood.Happy, 80, 2, new RelationshipEventProposal("FIRST_PROMISE", "Context A"));
        await runtime.ProcessTurnAsync(new CharacterTurnRequest(userId, charId, session.Id, "Promise me.", Guid.NewGuid()));

        var rel1 = await db.CharacterRelationships.AsNoTracking().FirstAsync(r => r.UserId == userId && r.CharacterId == charId);
        Assert.Single(rel1.Events);
        Assert.Equal("FIRST_PROMISE", rel1.Events.First().EventKey);

        // Turn 2: Propose same Event Key FIRST_PROMISE -> Ignored / Deduplicated
        scriptedLLM.EnqueueResponse("I promise again.", CharacterMood.Happy, 80, 2, new RelationshipEventProposal("FIRST_PROMISE", "Context B"));
        await runtime.ProcessTurnAsync(new CharacterTurnRequest(userId, charId, session.Id, "Promise again.", Guid.NewGuid()));

        var rel2 = await db.CharacterRelationships.AsNoTracking().FirstAsync(r => r.UserId == userId && r.CharacterId == charId);
        Assert.Single(rel2.Events); // Still exactly 1 event!
    }
}
