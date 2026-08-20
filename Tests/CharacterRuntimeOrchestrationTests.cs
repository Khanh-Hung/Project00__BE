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
    }

    [Fact]
    public async Task CharacterRuntime_Enforces_Idempotency_On_TurnId_Retries()
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
        var fakeLlmService = new FakeLLMService("Phản hồi lần đầu", CharacterMood.Happy, 60, 3);
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
            UserMessage: "Em nhớ anh",
            TurnId: fixedTurnId
        );

        // Turn 1
        var result1 = await runtime.ProcessTurnAsync(turnReq);
        Assert.Equal("Phản hồi lần đầu", result1.Reply);
        Assert.Equal(1, fakeLlmService.CallCount);

        // Turn 2: Retry with exact same TurnId
        var result2 = await runtime.ProcessTurnAsync(turnReq);
        Assert.Equal("Phản hồi lần đầu", result2.Reply);
        Assert.Equal(result1.MessageId, result2.MessageId);
        // LLM must NOT be called again
        Assert.Equal(1, fakeLlmService.CallCount);
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
        Assert.Null(result.AudioUrl);
        Assert.Null(result.ImageUrl);
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
        public int CallCount { get; private set; }

        public FakeLLMService(string reply, CharacterMood mood, int intensity, int delta)
        {
            _reply = reply;
            _mood = mood;
            _intensity = intensity;
            _delta = delta;
        }

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(RoleplayContext context, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new RoleplayTurnResult(_reply, _mood, _intensity, _delta, null));
        }

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, IReadOnlyCollection<CharacterMemory>? memories = null, CancellationToken ct = default)
        {
            return Task.FromResult(new RoleplayTurnResult(_reply, _mood, _intensity, _delta, null));
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
