using Application.Abstractions.Data;
using Application.Common;
using Application.DTOs;
using Application.Features.Chat.Commands.SendChatMessage;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.LLM.Prompts;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Project.Tests;

public class CharacterMemoryTests
{
    [Fact]
    public void Domain_CharacterMemory_Create_Enforces_Strict_Invariants()
    {
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // 1. Empty content throws ArgumentException
        Assert.Throws<ArgumentException>(() =>
            CharacterMemory.Create(charId, userId, "   ", MemoryType.Fact));

        // 2. Empty CharacterId/UserId throws ArgumentException
        Assert.Throws<ArgumentException>(() =>
            CharacterMemory.Create(Guid.Empty, userId, "Valid", MemoryType.Fact));
        Assert.Throws<ArgumentException>(() =>
            CharacterMemory.Create(charId, Guid.Empty, "Valid", MemoryType.Fact));

        // 3. Content > 1000 characters throws ArgumentException
        var longContent = new string('a', 1001);
        Assert.Throws<ArgumentException>(() =>
            CharacterMemory.Create(charId, userId, longContent, MemoryType.Fact));

        // 4. Out of range importance throws ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CharacterMemory.Create(charId, userId, "Valid", MemoryType.Fact, importance: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CharacterMemory.Create(charId, userId, "Valid", MemoryType.Fact, importance: 6));

        // 5. Out of range confidence throws ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CharacterMemory.Create(charId, userId, "Valid", MemoryType.Fact, confidence: -0.1m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CharacterMemory.Create(charId, userId, "Valid", MemoryType.Fact, confidence: 1.1m));

        // 6. Valid creation succeeds
        var memory = CharacterMemory.Create(
            characterId: charId,
            userId: userId,
            content: "User loves black coffee",
            type: MemoryType.Preference,
            importance: 4,
            confidence: 0.95m
        );

        Assert.Equal(4, memory.Importance);
        Assert.Equal(0.95m, memory.Confidence);
        Assert.Equal("User loves black coffee", memory.Content);
        Assert.Equal(MemoryType.Preference, memory.Type);
    }

    [Fact]
    public void Domain_MemoryCandidate_ValueObject_Enforces_Invariants()
    {
        // 1. Empty content throws ArgumentException
        Assert.Throws<ArgumentException>(() =>
            new MemoryCandidate("   ", MemoryType.Fact, 3, 0.9m));

        // 2. Content > 500 characters throws ArgumentException
        var longContent = new string('x', 501);
        Assert.Throws<ArgumentException>(() =>
            new MemoryCandidate(longContent, MemoryType.Fact, 3, 0.9m));

        // 3. Out of range importance throws ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoryCandidate("Valid", MemoryType.Fact, 0, 0.9m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoryCandidate("Valid", MemoryType.Fact, 6, 0.9m));

        // 4. Out of range confidence throws ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoryCandidate("Valid", MemoryType.Fact, 3, -0.1m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoryCandidate("Valid", MemoryType.Fact, 3, 1.1m));

        // 5. Valid candidate
        var candidate = new MemoryCandidate("User likes cats", MemoryType.Preference, 4, 0.92m);
        Assert.Equal("User likes cats", candidate.Content);
        Assert.Equal(MemoryType.Preference, candidate.Type);
        Assert.Equal(4, candidate.Importance);
        Assert.Equal(0.92m, candidate.Confidence);
    }

    [Fact]
    public void Validator_Rejects_Invalid_Candidates_And_Applies_Confidence_Policy()
    {
        var validator = new MemoryCandidateValidator(Options.Create(new MemoryExtractionOptions { MinConfidence = 0.60m }));

        // 1. Valid high-confidence candidate -> Accepted
        var valid = new MemoryCandidate("User has a brother", MemoryType.Fact, 3, 0.85m);
        Assert.True(validator.Validate(valid, out var reason1));
        Assert.Null(reason1);

        // 2. Weak confidence (< 0.50) -> Rejected
        var weak = new MemoryCandidate("User might like tea", MemoryType.Preference, 4, 0.40m);
        Assert.False(validator.Validate(weak, out var reason2));
        Assert.NotNull(reason2);

        // 3. Borderline confidence (0.55 < 0.60) with low importance (2) -> Rejected
        var borderlineLowImp = new MemoryCandidate("User said ok", MemoryType.Fact, 2, 0.55m);
        Assert.False(validator.Validate(borderlineLowImp, out var reason3));
        Assert.NotNull(reason3);

        // 4. Borderline confidence (0.55 < 0.60) with strong importance (4) -> Accepted
        var borderlineStrongImp = new MemoryCandidate("User confessed a deep secret", MemoryType.Secret, 4, 0.55m);
        Assert.True(validator.Validate(borderlineStrongImp, out var reason4));
        Assert.Null(reason4);
    }

    [Fact]
    public void Domain_CharacterMemory_UpdateDetails_And_MarkAccessed_Work()
    {
        var memory = CharacterMemory.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "User has a dog",
            MemoryType.Fact,
            importance: 2,
            confidence: 0.6m
        );

        memory.UpdateDetails(importance: 4, confidence: 0.95m, updatedContent: "User has a golden retriever");
        Assert.Equal(4, memory.Importance);
        Assert.Equal(0.95m, memory.Confidence);
        Assert.Equal("User has a golden retriever", memory.Content);

        // Invalid update values throw
        Assert.Throws<ArgumentOutOfRangeException>(() => memory.UpdateDetails(importance: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => memory.UpdateDetails(confidence: 2.0m));
        Assert.Throws<ArgumentException>(() => memory.UpdateDetails(updatedContent: ""));

        var now = DateTime.UtcNow;
        memory.MarkAccessed(now);
        Assert.Equal(now, memory.LastAccessedAt);
    }

    [Fact]
    public async Task MemoryService_Deduplication_Updates_Existing_Memory_Without_Duplication()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(context);
        var validator = new MemoryCandidateValidator();
        var memoryService = new MemoryService(unitOfWork, validator, NullLogger<MemoryService>.Instance);

        // Store initial candidate
        var candidates1 = new[]
        {
            new MemoryCandidate("User has a cat named Miu", MemoryType.Fact, 3, 0.8m)
        };
        var metrics1 = await memoryService.StoreCandidatesAsync(userId, charId, null, candidates1);
        Assert.Equal(1, metrics1.ExtractedCount);
        Assert.Equal(1, metrics1.AcceptedCount);
        Assert.Equal(1, metrics1.PersistedCount);
        Assert.Equal(0, metrics1.DuplicateCount);

        // Store duplicate candidate with differing case / whitespace and higher signals
        var candidates2 = new[]
        {
            new MemoryCandidate("  user  has a cat named   miu  ", MemoryType.Fact, 5, 0.98m)
        };
        var metrics2 = await memoryService.StoreCandidatesAsync(userId, charId, null, candidates2);
        Assert.Equal(1, metrics2.ExtractedCount);
        Assert.Equal(1, metrics2.AcceptedCount);
        Assert.Equal(0, metrics2.PersistedCount);
        Assert.Equal(1, metrics2.DuplicateCount);

        var memories = await memoryService.GetRelevantMemoriesAsync(userId, charId, 10);
        Assert.Single(memories);
        Assert.Equal(5, memories[0].Importance);
        Assert.Equal(0.98m, memories[0].Confidence);
    }

    [Fact]
    public async Task MemoryService_Retrieval_Applies_Diversity_And_Limits_Output()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(context);
        var validator = new MemoryCandidateValidator();
        var memoryService = new MemoryService(unitOfWork, validator, NullLogger<MemoryService>.Instance);

        var candidates = new[]
        {
            new MemoryCandidate("Fact 1", MemoryType.Fact, 5, 0.9m),
            new MemoryCandidate("Fact 2", MemoryType.Fact, 4, 0.9m),
            new MemoryCandidate("Fact 3", MemoryType.Fact, 4, 0.9m),
            new MemoryCandidate("Preference 1: Loves Tea", MemoryType.Preference, 4, 0.9m),
            new MemoryCandidate("Promise 1: Meet at garden", MemoryType.Promise, 5, 0.9m),
            new MemoryCandidate("Event 1: Walked under rain", MemoryType.Event, 3, 0.9m),
            new MemoryCandidate("Secret 1: Afraid of spiders", MemoryType.Secret, 4, 0.9m),
        };

        await memoryService.StoreCandidatesAsync(userId, charId, null, candidates);

        // Retrieve top 4 with diversity
        var retrieved = await memoryService.GetRelevantMemoriesAsync(userId, charId, maxCount: 4);

        Assert.Equal(4, retrieved.Count);
        var distinctTypes = retrieved.Select(m => m.Type).Distinct().Count();
        Assert.True(distinctTypes >= 3, "Retrieval should prioritize a diversity of memory types");
    }

    [Fact]
    public void RoleplayPrompts_Compiles_Relevant_Memories_Section_Properly()
    {
        var charId = Guid.NewGuid();
        var character = new Character(
            name: "Luna",
            title: "Phù thủy",
            avatarUrl: "https://example.com/luna.jpg",
            personalityPrompt: "Dịu dàng",
            greeting: "Chào!",
            category: "Phù thủy"
        );

        var memories = new List<CharacterMemory>
        {
            CharacterMemory.Create(charId, Guid.NewGuid(), "User has a cat named Miu", MemoryType.Fact, 4),
            CharacterMemory.Create(charId, Guid.NewGuid(), "User loves rainy days", MemoryType.Preference, 3),
            CharacterMemory.Create(charId, Guid.NewGuid(), "Promised to study magic together", MemoryType.Promise, 5)
        };

        var prompt = RoleplayPrompts.BuildSystemPrompt(character, null, memories);

        Assert.Contains("RELEVANT MEMORIES", prompt);
        Assert.Contains("[Fact] User has a cat named Miu", prompt);
        Assert.Contains("[Preference] User loves rainy days", prompt);
        Assert.Contains("[Promise] Promised to study magic together", prompt);
    }

    [Fact]
    public void MemoryExtractionTrigger_Respects_BatchSize_Policy()
    {
        var options = Options.Create(new MemoryExtractionOptions { BatchSize = 5, QueueCapacity = 10 });
        var trigger = new MemoryExtractionBackgroundService(
            scopeFactory: null!,
            logger: NullLogger<MemoryExtractionBackgroundService>.Instance,
            options: options
        );

        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var messages = new List<ChatMessageDto>
        {
            new(Guid.NewGuid(), MessageRole.User, "Hello", DateTime.UtcNow)
        };

        // UserMessageCount = 1..4 -> Not enqueued
        for (int i = 1; i <= 4; i++)
        {
            var job = new MemoryExtractionJob(sessionId, charId, userId, messages, UserMessageCount: i);
            var enqueued = trigger.NotifyMessageSent(job);
            Assert.False(enqueued, $"Message count {i} should NOT trigger extraction when BatchSize=5.");
        }

        // UserMessageCount = 5 -> Enqueued
        var job5 = new MemoryExtractionJob(sessionId, charId, userId, messages, UserMessageCount: 5);
        Assert.True(trigger.NotifyMessageSent(job5), "Message count 5 SHOULD trigger extraction.");

        // UserMessageCount = 6..9 -> Not enqueued
        for (int i = 6; i <= 9; i++)
        {
            var job = new MemoryExtractionJob(sessionId, charId, userId, messages, UserMessageCount: i);
            Assert.False(trigger.NotifyMessageSent(job));
        }

        // UserMessageCount = 10 -> Enqueued
        var job10 = new MemoryExtractionJob(sessionId, charId, userId, messages, UserMessageCount: 10);
        Assert.True(trigger.NotifyMessageSent(job10), "Message count 10 SHOULD trigger extraction.");
    }

    [Fact]
    public async Task SendChatMessage_Succeeds_When_Memory_Retrieval_Fails()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Luna", "Mage", "https://example.com/avatar.jpg", "Friendly", "Hello", "Fantasy") { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, userId, "Test Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var failingMemoryService = new FailingMemoryService();
        var fakeLLM = new FakeLLMService();
        var extractionTrigger = new DummyExtractionTrigger();
        var currentUserProvider = new DummyCurrentUserProvider(userId.ToString());
        var contextEngine = new RoleplayContextEngine(
            unitOfWork,
            failingMemoryService,
            currentUserProvider,
            NullLogger<RoleplayContextEngine>.Instance
        );
        var runtime = new CharacterRuntime(
            unitOfWork,
            contextEngine,
            fakeLLM,
            extractionTrigger,
            new VoicePromptCompiler(),
            new DummyVoiceService(),
            new VisualPromptCompiler(),
            new DummyImageService(),
            NullLogger<CharacterRuntime>.Instance
        );

        var handler = new SendChatMessageHandler(
            runtime,
            currentUserProvider,
            NullLogger<SendChatMessageHandler>.Instance
        );

        var result = await handler.Handle(
            new SendChatMessageCommand(new SendMessageRequest(session.Id, "Hello Luna")),
            CancellationToken.None
        );

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Mock AI reply", result.Value.AssistantMessage.Content);
    }

    private sealed class DummyCurrentUserProvider : Application.Abstractions.Auth.ICurrentUserProvider
    {
        public string? CurrentUserId { get; }
        public string? CurrentUserEmail => "test@example.com";
        public DummyCurrentUserProvider(string? currentUserId) => CurrentUserId = currentUserId;
    }

    private sealed class FailingMemoryService : IMemoryService
    {
        public Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(Guid userId, Guid characterId, int maxCount = 6, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated database failure during memory retrieval.");
        }

        public Task<MemoryExtractionMetrics> StoreCandidatesAsync(Guid userId, Guid characterId, Guid? sessionId, IEnumerable<MemoryCandidate> candidates, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated store error.");
        }
    }

    private sealed class DummyExtractionTrigger : IMemoryExtractionTrigger
    {
        public bool NotifyMessageSent(MemoryExtractionJob job) => true;
    }

    private sealed class FakeLLMService : ILLMService
    {
        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(RoleplayContext context, CancellationToken ct = default)
        {
            return Task.FromResult(new RoleplayTurnResult("Mock AI reply", CharacterMood.Happy, 50, 0, null));
        }

        public IAsyncEnumerable<string> GenerateRoleplayTurnStreamAsync(RoleplayContext context, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, IReadOnlyCollection<CharacterMemory>? memories = null, CancellationToken ct = default)
        {
            return Task.FromResult(new RoleplayTurnResult("Mock AI reply", CharacterMood.Happy, 50, 0, null));
        }

        public Task<string> GenerateRoleplayResponseAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, CancellationToken ct = default)
        {
            return Task.FromResult("Mock AI reply");
        }

        public Task<GeneratedCharacterDto> GenerateCharacterProfileAsync(string idea, string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRandomIdeasAsync(int count = 4, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRoleplaySuggestionsAsync(Character character, IReadOnlyCollection<ChatMessage> history, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateSceneImageAsync(GenerateSceneImageRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MemoryCandidate>> ExtractMemoryCandidatesAsync(Character character, IReadOnlyCollection<ChatMessageDto> recentMessages, CancellationToken ct = default) => Task.FromResult(new List<MemoryCandidate>());
    }

    private sealed class DummyVoiceService : IVoiceGenerationService
    {
        public Task<VoiceGenerationResult> GenerateVoiceAsync(VoiceGenerationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new VoiceGenerationResult("/uploads/audio/dummy.mp3", "audio/mpeg", 2));
    }

    private sealed class DummyImageService : IImageGenerationService
    {
        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default) =>
            Task.FromResult("https://example.com/dummy.jpg");
        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default) =>
            Task.FromResult("https://example.com/dummy.jpg");
    }
}
