using Application.Abstractions.Auth;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

public class TransactionalOutboxTests
{
    [Fact]
    public async Task CharacterRuntime_Enqueues_Outbox_Messages_Atomically_During_Turn()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = new CoreDbContext(options);
        var character = new Character(
            name: "Aria",
            title: "Songstress",
            avatarUrl: "https://example.com/aria.jpg",
            personalityPrompt: "Melodic",
            greeting: "La la la",
            category: "Fantasy",
            voiceProfile: new CharacterVoiceProfile("aria_voice"),
            visualIdentity: new CharacterVisualIdentity(Hair: "Golden")
        ) { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, userId, "Outbox Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var fakeUserProvider = new FakeCurrentUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var fakeLlmService = new FakeLLMService("Giai điệu này tặng bạn!", CharacterMood.Happy, 90, 3);
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
            new VisualStateResolver(unitOfWork, null, SceneCompositionTestHelper.CreatePipeline(context), NullLogger<VisualStateResolver>.Instance),
            NullLogger<CharacterRuntime>.Instance
        );

        var turnReq = new CharacterTurnRequest(
            UserId: userId,
            CharacterId: charId,
            SessionId: session.Id,
            UserMessage: "Hát cho mình nghe đi",
            TurnId: Guid.NewGuid(),
            Options: new CharacterTurnOptions(GenerateVoice: true, GenerateImage: true)
        );

        var result = await runtime.ProcessTurnAsync(turnReq);
        Assert.NotNull(result);

        // Verify that 3 outbox messages (Memory, Voice, Scene Image) were atomically committed to the database
        var outboxMessages = await context.OutboxMessages.ToListAsync();
        Assert.Equal(3, outboxMessages.Count);

        Assert.Contains(outboxMessages, m => m.EventType == OutboxEventTypes.MemoryExtraction && m.Status == OutboxStatus.Pending);
        Assert.Contains(outboxMessages, m => m.EventType == OutboxEventTypes.VoiceGeneration && m.Status == OutboxStatus.Pending);
        Assert.Contains(outboxMessages, m => m.EventType == OutboxEventTypes.SceneImageGeneration && m.Status == OutboxStatus.Pending);
    }

    [Fact]
    public async Task OutboxProcessor_Processes_Pending_Messages_Successfully()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var services = new ServiceCollection();
        services.AddDbContext<CoreDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IVoicePromptCompiler, VoicePromptCompiler>();
        services.AddSingleton<IVisualPromptCompiler, VisualPromptCompiler>();
        services.AddSingleton<IVoiceGenerationService, MockVoiceService>();
        services.AddSingleton<IImageGenerationService, MockImageService>();
        services.AddSingleton<IMemoryExtractionTrigger, MockMemoryExtractionTrigger>();
        services.AddLogging();

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        // Seed 2 outbox messages
        using (var seedScope = scopeFactory.CreateScope())
        {
            var ctx = seedScope.ServiceProvider.GetRequiredService<CoreDbContext>();
            var voicePayload = new VoiceGenerationOutboxPayload(
                TurnId: Guid.NewGuid(),
                CharacterId: Guid.NewGuid(),
                UserId: Guid.NewGuid(),
                VoiceProfile: new CharacterVoiceProfile("voice_1"),
                Mood: CharacterMood.Happy,
                MoodIntensity: 80,
                AffectionScore: 20,
                RelationshipStage: "Friend",
                RawText: "Hello world"
            );
            await ctx.OutboxMessages.AddAsync(new OutboxMessage(OutboxEventTypes.VoiceGeneration, System.Text.Json.JsonSerializer.Serialize(voicePayload)));
            await ctx.SaveChangesAsync();
        }

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        var processed = await processor.ProcessPendingOutboxMessagesAsync();

        Assert.Equal(1, processed);

        // Verify outbox message updated to Completed
        using (var verifyScope = scopeFactory.CreateScope())
        {
            var ctx = verifyScope.ServiceProvider.GetRequiredService<CoreDbContext>();
            var msg = await ctx.OutboxMessages.FirstAsync();
            Assert.Equal(OutboxStatus.Completed, msg.Status);
            Assert.NotNull(msg.ProcessedAt);
        }
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

    private sealed class FakeLLMService : ILLMService
    {
        private readonly string _reply;
        private readonly CharacterMood _mood;
        private readonly int _intensity;
        private readonly int _delta;

        public FakeLLMService(string reply, CharacterMood mood, int intensity, int delta)
        {
            _reply = reply;
            _mood = mood;
            _intensity = intensity;
            _delta = delta;
        }

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(RoleplayContext context, CancellationToken ct = default) =>
            Task.FromResult(new RoleplayTurnResult(_reply, _mood, _intensity, _delta, null));

        public IAsyncEnumerable<string> GenerateRoleplayTurnStreamAsync(RoleplayContext context, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, IReadOnlyCollection<CharacterMemory>? memories = null, CancellationToken ct = default) =>
            Task.FromResult(new RoleplayTurnResult(_reply, _mood, _intensity, _delta, null));

        public Task<string> GenerateRoleplayResponseAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, CancellationToken ct = default) =>
            Task.FromResult(_reply);

        public Task<GeneratedCharacterDto> GenerateCharacterProfileAsync(string idea, string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRandomIdeasAsync(int count = 4, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRoleplaySuggestionsAsync(Character character, IReadOnlyCollection<ChatMessage> history, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateSceneImageAsync(GenerateSceneImageRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MemoryCandidate>> ExtractMemoryCandidatesAsync(Character character, IReadOnlyCollection<ChatMessageDto> recentMessages, CancellationToken ct = default) => Task.FromResult(new List<MemoryCandidate>());
        public Task<ProactiveAiReachoutResult> GenerateProactiveReachoutAsync(Character character, UserProfile userProfile, CancellationToken ct = default) => Task.FromResult(new ProactiveAiReachoutResult("Hello", "Matched"));
    }

    private sealed class MockMemoryExtractionTrigger : IMemoryExtractionTrigger
    {
        public bool NotifyMessageSent(MemoryExtractionJob job) => true;
    }

    private sealed class MockVoiceService : IVoiceGenerationService
    {
        public Task<VoiceGenerationResult> GenerateVoiceAsync(VoiceGenerationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new VoiceGenerationResult("/audio/test.mp3", "audio/mpeg", 2));
    }

    private sealed class MockImageService : IImageGenerationService
    {
        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default) => Task.FromResult("https://example.com/test.jpg");
        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default) => Task.FromResult("https://example.com/test.jpg");
    }
}
