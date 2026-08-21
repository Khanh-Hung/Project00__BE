using System.Text.Json;
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

public class CharacterRuntimeStreamingTests
{
    [Fact]
    public async Task CharacterRuntime_Streams_Tokens_And_Lifecycle_Events_Successfully()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var fixedTurnId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Eldrin", "Ancient Sage", "https://example.com/eldrin.jpg", "Wise", "Greetings", "Fantasy") { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, userId, "Streaming Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);

        var streamedChunks = new[]
        {
            "```json\n{\"reply\": \"Chào ",
            "ngươi, kẻ tìm kiếm ",
            "tri thức.\", ",
            "\"mood\": \"Happy\", ",
            "\"moodIntensity\": 85, ",
            "\"affectionDelta\": 4, ",
            "\"event\": {\"key\": \"AncientKnowledge\", \"context\": \"Shared secret scroll\"}}\n```"
        };

        var fakeLlmService = new FakeStreamingLLMService(streamedChunks);
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
            UserMessage: "Xin chỉ bảo cho ta",
            TurnId: fixedTurnId
        );

        var receivedEvents = new List<CharacterStreamEvent>();

        await foreach (var streamEvent in runtime.ProcessTurnStreamAsync(turnReq))
        {
            receivedEvents.Add(streamEvent);
        }

        Assert.NotEmpty(receivedEvents);

        // Verify clean text token events were emitted without raw JSON syntax leakage
        var tokenEvents = receivedEvents.Where(e => e.Event == "token").ToList();
        Assert.NotEmpty(tokenEvents);
        var streamedDialogue = string.Join("", tokenEvents.Select(e => ((CharacterStreamTokenData)e.Data).Delta));
        Assert.Equal("Chào ngươi, kẻ tìm kiếm tri thức.", streamedDialogue);

        // Verify metadata event
        var metadataEvent = receivedEvents.FirstOrDefault(e => e.Event == "metadata");
        Assert.NotNull(metadataEvent);

        // Verify event_unlocked event
        var unlockedEvent = receivedEvents.FirstOrDefault(e => e.Event == "event_unlocked");
        Assert.NotNull(unlockedEvent);

        // Verify done event
        var doneEvent = receivedEvents.FirstOrDefault(e => e.Event == "done");
        Assert.NotNull(doneEvent);

        // Verify DB persistence
        var committedTurn = await context.CharacterTurns.FirstOrDefaultAsync(t => t.TurnId == fixedTurnId);
        Assert.NotNull(committedTurn);
        Assert.Equal("Chào ngươi, kẻ tìm kiếm tri thức.", committedTurn.AssistantReply);
        Assert.Equal("Happy", committedTurn.Mood);
        Assert.Equal(4, committedTurn.AffectionDelta);
    }

    [Fact]
    public async Task CharacterRuntime_Streams_With_Nested_Event_And_Mood_Before_Reply_Successfully()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var fixedTurnId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Eldrin", "Ancient Sage", "https://example.com/eldrin.jpg", "Wise", "Greetings", "Fantasy") { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, userId, "Nested Stream Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);

        // Chunks with nested event and mood BEFORE reply field
        var streamedChunks = new[]
        {
            "```json\n{\n  \"event\": {\"key\": \"TRUST_CONFIDANT\", \"context\": \"User shared secret\"},\n",
            "  \"mood\": \"Affectionate\",\n",
            "  \"moodIntensity\": 90,\n",
            "  \"affectionDelta\": 5,\n",
            "  \"reply\": \"Ta rất cảm động ",
            "khi ngươi mở lòng chia sẻ điều bí mật này.\"\n}\n```"
        };

        var fakeLlmService = new FakeStreamingLLMService(streamedChunks);
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
            UserMessage: "Ta có một bí mật muốn nói với người...",
            TurnId: fixedTurnId
        );

        var receivedEvents = new List<CharacterStreamEvent>();
        await foreach (var streamEvent in runtime.ProcessTurnStreamAsync(turnReq))
        {
            receivedEvents.Add(streamEvent);
        }

        Assert.NotEmpty(receivedEvents);

        // 1. Verify token events: strictly speech text only
        var tokenEvents = receivedEvents.Where(e => e.Event == "token").ToList();
        Assert.NotEmpty(tokenEvents);
        var streamedDialogue = string.Join("", tokenEvents.Select(e => ((CharacterStreamTokenData)e.Data).Delta));
        Assert.Equal("Ta rất cảm động khi ngươi mở lòng chia sẻ điều bí mật này.", streamedDialogue);

        // 2. Verify metadata event
        var metadataEvent = receivedEvents.FirstOrDefault(e => e.Event == "metadata");
        Assert.NotNull(metadataEvent);
        var metaJson = JsonSerializer.Serialize(metadataEvent.Data);
        using var metaDoc = JsonDocument.Parse(metaJson);
        Assert.Equal("Affectionate", metaDoc.RootElement.GetProperty("mood").GetString());
        Assert.Equal(90, metaDoc.RootElement.GetProperty("intensity").GetInt32());
        Assert.Equal(5, metaDoc.RootElement.GetProperty("affectionDelta").GetInt32());

        // 3. Verify event_unlocked event
        var unlockedEvent = receivedEvents.FirstOrDefault(e => e.Event == "event_unlocked");
        Assert.NotNull(unlockedEvent);
        var eventJson = JsonSerializer.Serialize(unlockedEvent.Data);
        using var eventDoc = JsonDocument.Parse(eventJson);
        Assert.Equal("TRUST_CONFIDANT", eventDoc.RootElement.GetProperty("eventKey").GetString());

        // 4. Verify done event
        var doneEvent = receivedEvents.FirstOrDefault(e => e.Event == "done");
        Assert.NotNull(doneEvent);

        // 5. Verify DB persistence
        var committedTurn = await context.CharacterTurns.FirstOrDefaultAsync(t => t.TurnId == fixedTurnId);
        Assert.NotNull(committedTurn);
        Assert.Equal("Ta rất cảm động khi ngươi mở lòng chia sẻ điều bí mật này.", committedTurn.AssistantReply);
        Assert.Equal("Affectionate", committedTurn.Mood);
        Assert.Equal(5, committedTurn.AffectionDelta);

        var rel = await context.CharacterRelationships.FirstOrDefaultAsync(r => r.UserId == userId && r.CharacterId == charId);
        Assert.NotNull(rel);
        Assert.Contains(rel.Events, e => e.EventKey == "TRUST_CONFIDANT");
    }

    [Fact]
    public async Task CharacterRuntime_Stream_Returns_Idempotent_Stream_On_Retry()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var fixedTurnId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Eldrin", "Ancient Sage", "https://example.com/eldrin.jpg", "Wise", "Greetings", "Fantasy") { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, userId, "Streaming Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork1 = new UnitOfWork(context);
        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine1 = new RoleplayContextEngine(unitOfWork1, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var fakeLlmService1 = new FakeStreamingLLMService(new[] { "Phản hồi ", "stream ", "lần đầu" });

        var runtime1 = new CharacterRuntime(
            unitOfWork1,
            contextEngine1,
            fakeLlmService1,
            new MockMemoryExtractionTrigger(),
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
            UserMessage: "Tin nhắn test stream",
            TurnId: fixedTurnId
        );

        // Stream 1
        var events1 = new List<CharacterStreamEvent>();
        await foreach (var evt in runtime1.ProcessTurnStreamAsync(turnReq))
        {
            events1.Add(evt);
        }
        Assert.NotEmpty(events1);
        Assert.Equal(1, fakeLlmService1.StreamCallCount);

        // Stream 2 with brand new context and runtime (Simulating replay on retry)
        await using var context2 = new ProjectDbContext(options);
        var unitOfWork2 = new UnitOfWork(context2);
        var contextEngine2 = new RoleplayContextEngine(unitOfWork2, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var fakeLlmService2 = new FakeStreamingLLMService(new[] { "Không được gọi" });

        var runtime2 = new CharacterRuntime(
            unitOfWork2,
            contextEngine2,
            fakeLlmService2,
            new MockMemoryExtractionTrigger(),
            new VoicePromptCompiler(),
            new MockVoiceService(),
            new VisualPromptCompiler(),
            new MockImageService(),
            NullLogger<CharacterRuntime>.Instance
        );

        var events2 = new List<CharacterStreamEvent>();
        await foreach (var evt in runtime2.ProcessTurnStreamAsync(turnReq))
        {
            events2.Add(evt);
        }

        // Must stream from idempotency snapshot without calling LLM
        Assert.Equal(0, fakeLlmService2.StreamCallCount);
        var tokenEvent2 = events2.First(e => e.Event == "token");
        Assert.NotNull(tokenEvent2);
        var doneEvent2 = events2.First(e => e.Event == "done");
        Assert.NotNull(doneEvent2);
    }

    private sealed class FakeStreamingLLMService : ILLMService
    {
        private readonly string[] _chunks;
        public int StreamCallCount { get; private set; }

        public FakeStreamingLLMService(string[] chunks)
        {
            _chunks = chunks;
        }

        public async IAsyncEnumerable<string> GenerateRoleplayTurnStreamAsync(
            RoleplayContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamCallCount++;
            foreach (var chunk in _chunks)
            {
                await Task.Yield();
                yield return chunk;
            }
        }

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(RoleplayContext context, CancellationToken ct = default) =>
            Task.FromResult(new RoleplayTurnResult(string.Join("", _chunks), CharacterMood.Neutral, 50, 0, null));

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, IReadOnlyCollection<CharacterMemory>? memories = null, CancellationToken ct = default) =>
            Task.FromResult(new RoleplayTurnResult(string.Join("", _chunks), CharacterMood.Neutral, 50, 0, null));

        public Task<string> GenerateRoleplayResponseAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, CancellationToken ct = default) =>
            Task.FromResult(string.Join("", _chunks));

        public Task<GeneratedCharacterDto> GenerateCharacterProfileAsync(string idea, string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRandomIdeasAsync(int count = 4, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRoleplaySuggestionsAsync(Character character, IReadOnlyCollection<ChatMessage> history, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateSceneImageAsync(GenerateSceneImageRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MemoryCandidate>> ExtractMemoryCandidatesAsync(Character character, IReadOnlyCollection<ChatMessageDto> recentMessages, CancellationToken ct = default) => Task.FromResult(new List<MemoryCandidate>());
        public Task<ProactiveAiReachoutResult> GenerateProactiveReachoutAsync(Character character, UserProfile userProfile, CancellationToken ct = default) => Task.FromResult(new ProactiveAiReachoutResult("Hello", "Matched"));
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
        public Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(Guid userId, Guid characterId, int maxCount = 6, string? queryText = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CharacterMemory>>(new List<CharacterMemory>());

        public Task<MemoryExtractionMetrics> StoreCandidatesAsync(Guid userId, Guid characterId, Guid? sessionId, IEnumerable<MemoryCandidate> candidates, CancellationToken ct = default) =>
            Task.FromResult(new MemoryExtractionMetrics(0, 0, 0, 0, 0));
    }

    private sealed class MockMemoryExtractionTrigger : IMemoryExtractionTrigger
    {
        public bool NotifyMessageSent(MemoryExtractionJob job) => true;
    }

    private sealed class MockVoiceService : IVoiceGenerationService
    {
        public Task<VoiceGenerationResult> GenerateVoiceAsync(VoiceProviderRequest request, CancellationToken ct = default) =>
            Task.FromResult(new VoiceGenerationResult("/audio/test.mp3", "audio/mpeg", TimeSpan.FromSeconds(2)));
    }

    private sealed class MockImageService : IImageGenerationService
    {
        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default) => Task.FromResult("https://example.com/test.jpg");
        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default) => Task.FromResult("https://example.com/test.jpg");
    }
}
