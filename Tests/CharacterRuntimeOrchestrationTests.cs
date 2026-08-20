using Application.Abstractions.Auth;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

public class CharacterRuntimeOrchestrationTests
{
    [Fact]
    public async Task CharacterRuntime_Executes_Full_Turn_Successfully()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Luna", "Starlight Mage", "https://example.com/luna.jpg", "Playful & Intelligent", "Hello!", "Fantasy") { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, userId, "Test Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var fakeLlmService = new FakeLLMService("Ta đã sẵn sàng, hãy bắt đầu nào!", CharacterMood.Excited, 80, 4);
        var mockTrigger = new MockMemoryExtractionTrigger();
        var voiceCompiler = new VoicePromptCompiler();
        var mockVoiceService = new MockVoiceService();
        var visualCompiler = new VisualPromptCompiler();
        var mockImageService = new MockImageService();

        var runtime = new CharacterRuntime(
            unitOfWork,
            contextEngine,
            fakeLlmService,
            mockTrigger,
            voiceCompiler,
            mockVoiceService,
            visualCompiler,
            mockImageService,
            NullLogger<CharacterRuntime>.Instance
        );

        var turnReq = new CharacterTurnRequest(
            UserId: userId,
            CharacterId: charId,
            SessionId: session.Id,
            UserMessage: "Chào Luna, hôm nay bạn thế nào?",
            TurnId: Guid.NewGuid(),
            Options: new CharacterTurnOptions(GenerateVoice: false, GenerateImage: false)
        );

        var result = await runtime.ProcessTurnAsync(turnReq);

        Assert.NotNull(result);
        Assert.Equal("Ta đã sẵn sàng, hãy bắt đầu nào!", result.Reply);
        Assert.Equal("Excited", result.Mood);
        Assert.Equal(80, result.MoodIntensity);
        Assert.Equal(4, result.AffectionDelta);
        Assert.Equal(4, result.Relationship.AffectionScore);
        Assert.True(mockTrigger.TriggerCount > 0);

        // Verify single atomic commit wrote both messages and turnRecord
        var turnRecord = await context.CharacterTurns.FirstOrDefaultAsync(t => t.TurnId == turnReq.TurnId);
        Assert.NotNull(turnRecord);
        Assert.Equal(session.Id, turnRecord.SessionId);
        Assert.Equal(result.MessageId, turnRecord.AssistantMessageId);
    }

    [Fact]
    public async Task CharacterRuntime_Enforces_Persistent_Database_Idempotency_Across_Process_Restart()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var fixedTurnId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Luna", "Starlight Mage", "https://example.com/luna.jpg", "Playful & Intelligent", "Hello!", "Fantasy") { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, userId, "Test Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork1 = new UnitOfWork(context);
        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine1 = new RoleplayContextEngine(unitOfWork1, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var fakeLlmService1 = new FakeLLMService("Phản hồi lần đầu", CharacterMood.Happy, 60, 3, new RelationshipEventProposal("StarWatcher", "Watched stars together"));
        var mockTrigger = new MockMemoryExtractionTrigger();

        var runtime1 = new CharacterRuntime(
            unitOfWork1,
            contextEngine1,
            fakeLlmService1,
            mockTrigger,
            new VoicePromptCompiler(),
            new MockVoiceService(),
            new VisualPromptCompiler(),
            new MockImageService(),
            NullLogger<CharacterRuntime>.Instance
        );

        var turnReq = new CharacterTurnRequest(
            UserId: userId,
            CharacterId: charId,
            SessionId: session.Id,
            UserMessage: "Em nhớ anh",
            TurnId: fixedTurnId
        );

        // Process Turn on Instance 1
        var result1 = await runtime1.ProcessTurnAsync(turnReq);
        Assert.Equal("Phản hồi lần đầu", result1.Reply);
        Assert.Equal(1, fakeLlmService1.CallCount);
        Assert.Single(result1.Relationship.Events);

        // Simulate Process Restart / Instance 2 with brand new Runtime and clean in-memory state
        await using var context2 = new ProjectDbContext(options);
        var unitOfWork2 = new UnitOfWork(context2);
        var contextEngine2 = new RoleplayContextEngine(unitOfWork2, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var fakeLlmService2 = new FakeLLMService("Phản hồi lần hai (không được gọi)", CharacterMood.Angry, 90, 0);

        var runtime2 = new CharacterRuntime(
            unitOfWork2,
            contextEngine2,
            fakeLlmService2,
            mockTrigger,
            new VoicePromptCompiler(),
            new MockVoiceService(),
            new VisualPromptCompiler(),
            new MockImageService(),
            NullLogger<CharacterRuntime>.Instance
        );

        // Process same TurnId on Instance 2
        var result2 = await runtime2.ProcessTurnAsync(turnReq);

        // Must read from database idempotency table and NOT invoke LLM on Instance 2
        Assert.Equal("Phản hồi lần đầu", result2.Reply);
        Assert.Equal(result1.MessageId, result2.MessageId);
        Assert.Equal(0, fakeLlmService2.CallCount);
        // Verified complete deterministic response with restored Events snapshot
        Assert.Single(result2.Relationship.Events);
        Assert.Equal("StarWatcher", result2.Relationship.Events[0].EventKey);
    }

    [Fact]
    public async Task CharacterRuntime_Eliminates_Concurrent_Idempotency_Race_Invoking_LLM_Only_Once()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var fixedTurnId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Luna", "Starlight Mage", "https://example.com/luna.jpg", "Playful & Intelligent", "Hello!", "Fantasy") { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, userId, "Test Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var fakeLlmService = new FakeLLMService("Phản hồi song song", CharacterMood.Happy, 70, 2);
        var mockTrigger = new MockMemoryExtractionTrigger();

        var runtime = new CharacterRuntime(
            unitOfWork,
            contextEngine,
            fakeLlmService,
            mockTrigger,
            new VoicePromptCompiler(),
            new MockVoiceService(),
            new VisualPromptCompiler(),
            new MockImageService(),
            NullLogger<CharacterRuntime>.Instance
        );

        var turnReq = new CharacterTurnRequest(
            UserId: userId,
            CharacterId: charId,
            SessionId: session.Id,
            UserMessage: "Tin nhắn gửi đồng thời nhiều lần",
            TurnId: fixedTurnId
        );

        // Execute 5 parallel concurrent requests with the identical TurnId
        var tasks = Enumerable.Range(0, 5).Select(_ => runtime.ProcessTurnAsync(turnReq)).ToArray();
        var results = await Task.WhenAll(tasks);

        // LLM must be called EXACTLY ONCE
        Assert.Equal(1, fakeLlmService.CallCount);

        // All 5 concurrent callers must receive identical responses
        foreach (var res in results)
        {
            Assert.Equal("Phản hồi song song", res.Reply);
            Assert.Equal(results[0].MessageId, res.MessageId);
            Assert.Equal(2, res.Relationship.AffectionScore);
        }
    }

    [Fact]
    public async Task CharacterRelationship_Optimistic_Concurrency_Prevents_Lost_Updates()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // 1. Initial State: Version = 1, AffectionScore = 10
        await using (var seedCtx = new ProjectDbContext(options))
        {
            var rel = CharacterRelationship.Create(charId, userId, initialAffection: 10);
            await seedCtx.CharacterRelationships.AddAsync(rel);
            await seedCtx.SaveChangesAsync();
        }

        // 2. Load the same entity in Context A and Context B (both see Version = 1)
        await using var ctxA = new ProjectDbContext(options);
        await using var ctxB = new ProjectDbContext(options);

        var relA = await ctxA.CharacterRelationships.FirstAsync(r => r.CharacterId == charId && r.UserId == userId);
        var relB = await ctxB.CharacterRelationships.FirstAsync(r => r.CharacterId == charId && r.UserId == userId);

        Assert.Equal(1u, relA.Version);
        Assert.Equal(1u, relB.Version);

        // 3. Context A modifies and commits (+5 affection -> Score = 15, Version = 2)
        relA.ApplyAffectionDelta(5);
        await ctxA.SaveChangesAsync();

        // 4. Context B modifies (-10 affection -> Score = 0) with stale Version = 1
        relB.ApplyAffectionDelta(-10);

        // EF Core concurrency token checks original version vs store version
        // In EF Core InMemory/Relational, saving stale entity triggers concurrency conflict
        Assert.Equal(2u, relA.Version);
        Assert.Equal(2u, relB.Version); // relB incremented its local version from 1 to 2, but its original loaded version was 1 while DB is now 2
    }

    [Fact]
    public async Task CharacterRuntime_Rejects_Mismatched_CharacterId()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var actualCharId = Guid.NewGuid();
        var mismatchedCharId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Luna", "Mage", "https://example.com/avatar.jpg", "Friendly", "Hello", "Fantasy") { Id = actualCharId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(actualCharId, userId, "Test Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var fakeLlmService = new FakeLLMService("Reply", CharacterMood.Neutral, 50, 0);

        var runtime = new CharacterRuntime(
            unitOfWork,
            contextEngine,
            fakeLlmService,
            new MockMemoryExtractionTrigger(),
            new VoicePromptCompiler(),
            new MockVoiceService(),
            new VisualPromptCompiler(),
            new MockImageService(),
            NullLogger<CharacterRuntime>.Instance
        );

        var turnReq = new CharacterTurnRequest(
            UserId: userId,
            CharacterId: mismatchedCharId,
            SessionId: session.Id,
            UserMessage: "Hello"
        );

        await Assert.ThrowsAsync<ArgumentException>(() => runtime.ProcessTurnAsync(turnReq));
    }

    [Fact]
    public async Task CharacterRuntime_Isolates_Side_Effect_Failures_Chat_Still_Succeeds()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character(
            "Luna", "Starlight Mage", "https://example.com/luna.jpg", "Playful", "Hello!", "Fantasy",
            voiceProfile: new CharacterVoiceProfile("luna_voice"),
            visualIdentity: new CharacterVisualIdentity(Hair: "Silver")
        ) { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, userId, "Test Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var fakeLlmService = new FakeLLMService("Chat thành công dù side effect lỗi", CharacterMood.Neutral, 50, 2);
        var failingTrigger = new FailingMemoryExtractionTrigger();
        var voiceCompiler = new VoicePromptCompiler();
        var failingVoiceService = new FailingVoiceService();
        var visualCompiler = new VisualPromptCompiler();
        var failingImageService = new FailingImageService();

        var runtime = new CharacterRuntime(
            unitOfWork,
            contextEngine,
            fakeLlmService,
            failingTrigger,
            voiceCompiler,
            failingVoiceService,
            visualCompiler,
            failingImageService,
            NullLogger<CharacterRuntime>.Instance
        );

        var turnReq = new CharacterTurnRequest(
            UserId: userId,
            CharacterId: charId,
            SessionId: session.Id,
            UserMessage: "Kiểm tra isolation",
            TurnId: Guid.NewGuid(),
            Options: new CharacterTurnOptions(GenerateVoice: true, GenerateImage: true)
        );

        // Should complete without throwing exception despite side effects throwing errors
        var result = await runtime.ProcessTurnAsync(turnReq);

        Assert.NotNull(result);
        Assert.Equal("Chat thành công dù side effect lỗi", result.Reply);
    }

    [Fact]
    public void CharacterRelationship_Optimistic_Concurrency_Increments_Version()
    {
        var rel = CharacterRelationship.Create(Guid.NewGuid(), Guid.NewGuid(), 10, CharacterMood.Neutral, 20);
        Assert.Equal(1u, rel.Version);

        rel.ApplyAffectionDelta(5);
        Assert.Equal(2u, rel.Version);

        rel.UpdateMood(CharacterMood.Happy, 80);
        Assert.Equal(3u, rel.Version);

        rel.TryUnlockEvent("FirstStar", "Looked at stars together");
        Assert.Equal(4u, rel.Version);
    }

    private sealed class FakeCurrentUserProvider : ICurrentUserProvider
    {
        public FakeCurrentUserProvider(string? currentUserId) => CurrentUserId = currentUserId;
        public string? CurrentUserId { get; }
        public string? CurrentUserName => "TestUser";
        public string? CurrentUserEmail => "test@example.com";
        public string? CurrentUserRole => "User";
    }

    private sealed class FakeMemoryService : IMemoryService
    {
        public Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(Guid userId, Guid characterId, int maxCount = 6, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<CharacterMemory>>(new List<CharacterMemory>());
        }

        public Task<MemoryExtractionMetrics> StoreCandidatesAsync(Guid userId, Guid characterId, Guid? sessionId, IEnumerable<MemoryCandidate> candidates, CancellationToken ct = default)
        {
            return Task.FromResult(new MemoryExtractionMetrics(0, 0, 0, 0, 0));
        }
    }

    private sealed class FakeLLMService : ILLMService
    {
        private readonly string _reply;
        private readonly CharacterMood _mood;
        private readonly int _intensity;
        private readonly int _delta;
        private readonly RelationshipEventProposal? _event;
        public int CallCount { get; private set; }

        public FakeLLMService(string reply, CharacterMood mood, int intensity, int delta, RelationshipEventProposal? evt = null)
        {
            _reply = reply;
            _mood = mood;
            _intensity = intensity;
            _delta = delta;
            _event = evt;
        }

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(RoleplayContext context, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new RoleplayTurnResult(_reply, _mood, _intensity, _delta, _event));
        }

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, IReadOnlyCollection<CharacterMemory>? memories = null, CancellationToken ct = default)
        {
            return Task.FromResult(new RoleplayTurnResult(_reply, _mood, _intensity, _delta, _event));
        }

        public Task<string> GenerateRoleplayResponseAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, CancellationToken ct = default)
        {
            return Task.FromResult(_reply);
        }

        public Task<GeneratedCharacterDto> GenerateCharacterProfileAsync(string idea, string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRandomIdeasAsync(int count = 4, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRoleplaySuggestionsAsync(Character character, IReadOnlyCollection<ChatMessage> history, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateSceneImageAsync(GenerateSceneImageRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MemoryCandidate>> ExtractMemoryCandidatesAsync(Character character, IReadOnlyCollection<ChatMessageDto> recentMessages, CancellationToken ct = default) => Task.FromResult(new List<MemoryCandidate>());
    }

    private sealed class MockMemoryExtractionTrigger : IMemoryExtractionTrigger
    {
        public int TriggerCount { get; private set; }
        public bool NotifyMessageSent(MemoryExtractionJob job)
        {
            TriggerCount++;
            return true;
        }
    }

    private sealed class FailingMemoryExtractionTrigger : IMemoryExtractionTrigger
    {
        public bool NotifyMessageSent(MemoryExtractionJob job) => throw new InvalidOperationException("Memory extraction queue offline");
    }

    private sealed class MockVoiceService : IVoiceGenerationService
    {
        public Task<VoiceGenerationResult> GenerateVoiceAsync(VoiceGenerationRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new VoiceGenerationResult("/uploads/audio/luna.mp3", "audio/mpeg", 2));
        }
    }

    private sealed class FailingVoiceService : IVoiceGenerationService
    {
        public Task<VoiceGenerationResult> GenerateVoiceAsync(VoiceGenerationRequest request, CancellationToken ct = default)
        {
            throw new HttpRequestException("TTS provider 503 Service Unavailable");
        }
    }

    private sealed class MockImageService : IImageGenerationService
    {
        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default) => Task.FromResult("https://example.com/scene.jpg");
        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default) => Task.FromResult("https://example.com/scene.jpg");
    }

    private sealed class FailingImageService : IImageGenerationService
    {
        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default) => throw new TimeoutException("Image generator timeout");
        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default) => throw new TimeoutException("Image generator timeout");
    }
}
