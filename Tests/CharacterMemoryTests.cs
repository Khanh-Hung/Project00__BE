using Application.Abstractions.Data;
using Application.DTOs;
using Application.Features.Chat.Commands.SendChatMessage;
using Application.Interfaces;
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
        var memoryService = new MemoryService(unitOfWork, NullLogger<MemoryService>.Instance);

        // Store initial candidate
        var candidates1 = new[]
        {
            new MemoryCandidate("User has a cat named Miu", MemoryType.Fact, Importance: 3, Confidence: 0.8m)
        };
        var added1 = await memoryService.StoreCandidatesAsync(userId, charId, null, candidates1);
        Assert.Equal(1, added1);

        // Store duplicate candidate with differing case / whitespace and higher signals
        var candidates2 = new[]
        {
            new MemoryCandidate("  user  has a cat named   miu  ", MemoryType.Fact, Importance: 5, Confidence: 0.98m)
        };
        var added2 = await memoryService.StoreCandidatesAsync(userId, charId, null, candidates2);
        Assert.Equal(0, added2); // 0 added, updated existing

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
        var memoryService = new MemoryService(unitOfWork, NullLogger<MemoryService>.Instance);

        var candidates = new[]
        {
            new MemoryCandidate("Fact 1", MemoryType.Fact, Importance: 5),
            new MemoryCandidate("Fact 2", MemoryType.Fact, Importance: 4),
            new MemoryCandidate("Fact 3", MemoryType.Fact, Importance: 4),
            new MemoryCandidate("Preference 1: Loves Tea", MemoryType.Preference, Importance: 4),
            new MemoryCandidate("Promise 1: Meet at garden", MemoryType.Promise, Importance: 5),
            new MemoryCandidate("Event 1: Walked under rain", MemoryType.Event, Importance: 3),
            new MemoryCandidate("Secret 1: Afraid of spiders", MemoryType.Secret, Importance: 4),
        };

        await memoryService.StoreCandidatesAsync(userId, charId, null, candidates);

        // Retrieve top 4 with diversity
        var retrieved = await memoryService.GetRelevantMemoriesAsync(userId, charId, maxCount: 4);

        Assert.Equal(4, retrieved.Count);
        // Verify diversity: multiple types should be included instead of just all facts
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
        var session = new ChatSession(charId, userId, "Test Session");
        context.Characters.Add(character);
        context.ChatSessions.Add(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);

        // Failing memory service
        var failingMemoryService = new FailingMemoryService();
        var fakeLlmService = new FakeLLMService();
        var dummyTrigger = new DummyExtractionTrigger();

        var handler = new SendChatMessageHandler(unitOfWork, fakeLlmService, failingMemoryService, dummyTrigger);
        var command = new SendChatMessageCommand(new SendMessageRequest(session.Id, "Hello Luna!"));

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Mock AI reply", result.Value.AssistantMessage.Content);
    }

    [Fact]
    public async Task EntityFrameworkCore_Persists_And_Loads_CharacterMemory()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var memory = CharacterMemory.Create(
            characterId: charId,
            userId: userId,
            content: "User shared a secret about their past",
            type: MemoryType.Secret,
            importance: 5,
            confidence: 0.95m,
            sourceSessionId: sessionId
        );

        Guid memoryId;
        await using (var context = new ProjectDbContext(options))
        {
            context.CharacterMemories.Add(memory);
            await context.SaveChangesAsync();
            memoryId = memory.Id;
        }

        await using (var context = new ProjectDbContext(options))
        {
            var loaded = await context.CharacterMemories.FindAsync(memoryId);
            Assert.NotNull(loaded);
            Assert.Equal("User shared a secret about their past", loaded.Content);
            Assert.Equal(MemoryType.Secret, loaded.Type);
            Assert.Equal(5, loaded.Importance);
            Assert.Equal(0.95m, loaded.Confidence);
            Assert.Equal(sessionId, loaded.SourceSessionId);
        }
    }

    private sealed class FailingMemoryService : IMemoryService
    {
        public Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(Guid userId, Guid characterId, int maxCount = 6, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated database timeout or connection error.");
        }

        public Task<int> StoreCandidatesAsync(Guid userId, Guid characterId, Guid? sessionId, IEnumerable<MemoryCandidate> candidates, CancellationToken ct = default)
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
        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, ChatSession? session = null, IReadOnlyCollection<CharacterMemory>? memories = null, CancellationToken ct = default)
        {
            return Task.FromResult(new RoleplayTurnResult("Mock AI reply", "Vui vẻ", 2));
        }

        public Task<string> GenerateRoleplayResponseAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, ChatSession? session = null, CancellationToken ct = default)
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
}
