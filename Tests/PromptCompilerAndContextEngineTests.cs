using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
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

public class PromptCompilerAndContextEngineTests
{
    [Fact]
    public async Task RoleplayContextEngine_Enforces_Budgets_For_Messages_And_Memories()
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
        // Add 25 messages
        for (int i = 1; i <= 25; i++)
        {
            session.AddUserMessage($"User message {i}");
            session.AddAssistantMessage($"AI response {i}");
        }
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var fakeMemoryService = new FakeMemoryService();
        var currentUserProvider = new FakeCurrentUserProvider(userId.ToString());

        var engine = new RoleplayContextEngine(
            unitOfWork,
            fakeMemoryService,
            currentUserProvider,
            NullLogger<RoleplayContextEngine>.Instance
        );

        var roleplayContext = await engine.BuildContextAsync(session.Id, "Latest user prompt", userId);

        Assert.NotNull(roleplayContext);
        Assert.Equal(10, roleplayContext.RecentMessages.Count); // Budget limit: 10
        Assert.Equal("AI response 25", roleplayContext.RecentMessages.Last().Content);
        Assert.Equal(6, roleplayContext.Memories.Count); // Budget limit: 6
    }

    [Fact]
    public async Task RoleplayContextEngine_Strictly_Isolates_Memories_Between_Users()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Luna", "Mage", "https://example.com/avatar.jpg", "Friendly", "Hello", "Fantasy") { Id = charId };
        await context.Characters.AddAsync(character);

        // Memory belonging to User A
        var memoryA = CharacterMemory.Create(charId, userA, "Secret belonging to User A", MemoryType.Secret, 5);
        await context.CharacterMemories.AddAsync(memoryA);

        // Memory belonging to User B
        var memoryB = CharacterMemory.Create(charId, userB, "Preference of User B", MemoryType.Preference, 4);
        await context.CharacterMemories.AddAsync(memoryB);

        var sessionB = new ChatSession(charId, userB, "User B Session");
        sessionB.AddUserMessage("Hello Luna");
        await context.ChatSessions.AddAsync(sessionB);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var validator = new MemoryCandidateValidator();
        var realMemoryService = new MemoryService(unitOfWork, validator, NullLogger<MemoryService>.Instance);
        var currentUserProviderB = new FakeCurrentUserProvider(userB.ToString());

        var engine = new RoleplayContextEngine(
            unitOfWork,
            realMemoryService,
            currentUserProviderB,
            NullLogger<RoleplayContextEngine>.Instance
        );

        // Build context for User B
        var contextB = await engine.BuildContextAsync(sessionB.Id, "How are you?", userB);

        Assert.NotNull(contextB);
        Assert.Single(contextB.Memories);
        Assert.Equal("Preference of User B", contextB.Memories[0].Content);
        Assert.DoesNotContain(contextB.Memories, m => m.Content.Contains("User A"));
    }

    [Fact]
    public void PromptCompiler_Compiles_All_6_Layers_In_Correct_Priority()
    {
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var character = new Character("Luna", "Mage", "https://example.com/avatar.jpg", "Friendly and caring mage", "Hello!", "Fantasy") { Id = charId };
        character.SetBlueprint(new CharacterBlueprint(
            new PsychologyProfile("Reach master wizard", "Failing her friends", "Doubts magical talent", "Kindness always wins", "Duty vs personal dreams", "Compassion, Loyalty"),
            new BehaviorProfile("Beams with joy", "Quiets down and observes", "Blushes and speaks sternly", "Pouts playfully", "Smiles softly", "Maintains polite distance"),
            new ExpressionProfile("Soft, melodic tone", "Polite and gentle", "Playful teasing", EmojiUsage: null, new List<string> { "Ara ara~", "Thật là..." }),
            new CharacterRules(new List<string> { "Protect the weak" }, new List<string> { "Never use dark magic" }, "Always state true thoughts gently", new List<string> { "Do not tolerate cruelty" })
        ));

        var relationship = CharacterRelationship.Create(userId, charId, 75, CharacterMood.Happy);
        relationship.TryUnlockEvent("FirstMagicDuel", "Fought together against shadow wolves in the ancient forest");
        relationship.TryUnlockEvent("SharedSecretUnderStars", "Watched the stars on the observatory tower");

        var memories = new List<CharacterMemory>
        {
            CharacterMemory.Create(charId, userId, "User loves black tea with honey", MemoryType.Preference, 4),
            CharacterMemory.Create(charId, userId, "User promised to help repair the telescope", MemoryType.Promise, 5)
        };

        var session = new ChatSession(charId, userId, "Test Session");
        session.AddUserMessage("Chào Luna, hôm nay thế nào?");
        session.AddAssistantMessage("Mình vẫn khỏe, cảm ơn cậu!");

        var roleplayContext = new RoleplayContext(
            character,
            relationship,
            memories,
            session.Messages,
            "Cậu có muốn cùng đi dạo không?",
            session
        );

        var compiler = new PromptCompiler();
        var systemPrompt = compiler.CompileSystemPrompt(roleplayContext);
        var conversationContents = compiler.CompileConversationContents(roleplayContext);

        // 1. Layer 1: Psychological Blueprint
        Assert.Contains("[LAYER 1: DEEP PSYCHOLOGICAL BLUEPRINT]", systemPrompt);
        Assert.Contains("Reach master wizard", systemPrompt);

        // 2. Layer 2: Authoritative Rules
        Assert.Contains("[LAYER 2: AUTHORITATIVE CHARACTER RULES & ANTI-SYCOPHANCY]", systemPrompt);
        Assert.Contains("Always state true thoughts gently", systemPrompt);

        // 3. Layer 3: Dynamic Relationship State
        Assert.Contains("[LAYER 3: DYNAMIC RELATIONSHIP STATE & INTIMACY STATUS]", systemPrompt);
        Assert.Contains("Tri Kỷ & Tin Cậy", systemPrompt);
        Assert.Contains("FirstMagicDuel", systemPrompt);

        // 4. Layer 4: Relevant Long-Term Memories
        Assert.Contains("[LAYER 4: RELEVANT LONG-TERM MEMORIES", systemPrompt);
        Assert.Contains("User loves black tea with honey", systemPrompt);

        // 5. Layer 5: 3-Layer Roleplay Guidelines
        Assert.Contains("[LAYER 5: PSYCHOLOGICAL 3-LAYER ROLEPLAY GUIDELINES]", systemPrompt);
        Assert.Contains("【Inner Thoughts / Độc thoại nội tâm】", systemPrompt);
        Assert.Contains("【Actions & Micro-Expressions / Cử chỉ & Biểu cảm】", systemPrompt);

        // 6. Layer 6: Structured Output JSON Schema
        Assert.Contains("[LAYER 6: STRUCTURED OUTPUT JSON SCHEMA SPECIFICATION]", systemPrompt);
        Assert.Contains("\"affectionDelta\"", systemPrompt);

        // Verify conversation history
        Assert.Equal(3, conversationContents.Count); // 2 previous messages + 1 current user prompt
    }

    private sealed class FakeMemoryService : IMemoryService
    {
        public Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(Guid userId, Guid characterId, int maxCount = 6, CancellationToken ct = default)
        {
            var list = new List<CharacterMemory>();
            for (int i = 1; i <= maxCount; i++)
            {
                list.Add(CharacterMemory.Create(characterId, userId, $"Memory item {i}", MemoryType.Fact, 3));
            }
            return Task.FromResult<IReadOnlyList<CharacterMemory>>(list);
        }

        public Task<MemoryExtractionMetrics> StoreCandidatesAsync(Guid userId, Guid characterId, Guid? sessionId, IEnumerable<MemoryCandidate> candidates, CancellationToken ct = default)
        {
            return Task.FromResult(new MemoryExtractionMetrics(0, 0, 0, 0, 0));
        }
    }

    private sealed class FakeCurrentUserProvider : ICurrentUserProvider
    {
        public string? CurrentUserId { get; }
        public string? CurrentUserEmail => "test@example.com";
        public FakeCurrentUserProvider(string? currentUserId) => CurrentUserId = currentUserId;
    }
}
