using Application.Abstractions.Data;
using Application.DTOs;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.LLM.Prompts;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

public class CharacterMemoryTests
{
    [Fact]
    public void Domain_CharacterMemory_Create_Enforces_Invariants()
    {
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // 1. Empty content throws
        Assert.Throws<ArgumentException>(() =>
            CharacterMemory.Create(charId, userId, "   ", MemoryType.Fact));

        // 2. Empty CharacterId/UserId throws
        Assert.Throws<ArgumentException>(() =>
            CharacterMemory.Create(Guid.Empty, userId, "Valid", MemoryType.Fact));
        Assert.Throws<ArgumentException>(() =>
            CharacterMemory.Create(charId, Guid.Empty, "Valid", MemoryType.Fact));

        // 3. Clamping importance and confidence
        var memory = CharacterMemory.Create(
            characterId: charId,
            userId: userId,
            content: "User loves black coffee",
            type: MemoryType.Preference,
            importance: 99, // Should clamp to 5
            confidence: 2.5m // Should clamp to 1.0
        );

        Assert.Equal(5, memory.Importance);
        Assert.Equal(1.0m, memory.Confidence);
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
}
