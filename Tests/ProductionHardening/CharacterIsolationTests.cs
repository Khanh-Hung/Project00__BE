using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Common;
using Application.Contracts.CognitiveCycle;
using Application.Enums;
using Application.Interfaces;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.CognitiveCycle;
using Infrastructure.Services.Safety;
using Infrastructure.Services.SocialPresence;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.ProductionHardening;

/// <summary>
/// Character Isolation Test Suite (Section VIII)
/// Verifies that multi-tenant and multi-character isolation boundaries are absolute.
/// Character A cannot read, mutate, or leak state, memories, relationships,
/// personalities, goals, social presence, autonomous ticks, or world events belonging to Character B.
/// </summary>
public class CharacterIsolationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterIsolationTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
        db.EnsureLifeSimulationTriggersCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task CharacterState_Isolation_CharacterACannotReadOrMutateCharacterB()
    {
        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var db = new CoreDbContext(_options))
        {
            db.CharacterStates.Add(new CharacterState(charA, now, hunger: 20m, energy: 90m, stress: 10m));
            db.CharacterStates.Add(new CharacterState(charB, now, hunger: 80m, energy: 30m, stress: 70m));
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

            // Read isolation: querying CharA returns only CharA's state
            var stateA = await stateService.GetAsync(charA);
            Assert.NotNull(stateA);
            Assert.Equal(20m, stateA.Hunger);

            var stateB = await stateService.GetAsync(charB);
            Assert.NotNull(stateB);
            Assert.Equal(80m, stateB.Hunger);

            // Mutation isolation: applying transition to CharA MUST NOT mutate CharB
            var transitionContext = new StateTransitionContext(
                ExecutionId: Guid.NewGuid(),
                SourceType: "Test",
                SourceId: "Test1",
                Reason: "Feed Character A"
            );

            var deltaA = new CharacterStateDelta(hungerDelta: 30m);
            var resultA = await transitionService.TransitionAsync(charA, deltaA, transitionContext, now);

            Assert.Equal(StateTransitionResultStatus.Applied, resultA.Status);
            Assert.Equal(50m, resultA.Snapshot!.Hunger);

            // Verify CharB remains untouched
            var reloadedB = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charB);
            Assert.Equal(80m, reloadedB.Hunger);
            Assert.Equal(30m, reloadedB.Energy);
            Assert.Equal(70m, reloadedB.Stress);
            Assert.Equal(1, reloadedB.Version);
        }
    }

    [Fact]
    public async Task CharacterMemory_Isolation_CharacterACannotRetrieveCharacterBMemories()
    {
        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var db = new CoreDbContext(_options))
        {
            var memA = CharacterMemory.Create(
                characterId: charA,
                userId: userId,
                content: "Secret of Character A",
                type: MemoryType.Fact,
                importance: 5,
                confidence: 0.9m);

            var memB = CharacterMemory.Create(
                characterId: charB,
                userId: userId,
                content: "Secret of Character B",
                type: MemoryType.Fact,
                importance: 5,
                confidence: 0.9m);

            db.CharacterMemories.AddRange(memA, memB);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var retrievalService = new CharacterMemoryRetrievalService(db, NullLogger<CharacterMemoryRetrievalService>.Instance);

            var perceptionA = new CharacterPerceptionContext(now, charA, null);
            var memoryContextA = await retrievalService.RetrieveRelevantAsync(charA, perceptionA);

            Assert.Single(memoryContextA.RelevantMemories);
            Assert.Contains("Secret of Character A", memoryContextA.RelevantMemories[0].Content);
            Assert.DoesNotContain(memoryContextA.RelevantMemories, m => m.Content.Contains("Character B"));

            var perceptionB = new CharacterPerceptionContext(now, charB, null);
            var memoryContextB = await retrievalService.RetrieveRelevantAsync(charB, perceptionB);

            Assert.Single(memoryContextB.RelevantMemories);
            Assert.Contains("Secret of Character B", memoryContextB.RelevantMemories[0].Content);
            Assert.DoesNotContain(memoryContextB.RelevantMemories, m => m.Content.Contains("Character A"));
        }
    }

    [Fact]
    public async Task CharacterRelationship_Isolation_CharacterACannotAccessCharacterBRelationships()
    {
        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var db = new CoreDbContext(_options))
        {
            var relA = CharacterRelationship.Create(charA, userId, initialAffection: 10, initialMood: CharacterMood.Happy, initialMoodIntensity: 50, initialTimestamp: now);
            var relB = CharacterRelationship.Create(charB, userId, initialAffection: 80, initialMood: CharacterMood.Excited, initialMoodIntensity: 90, initialTimestamp: now);

            db.CharacterRelationships.AddRange(relA, relB);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterRelationshipRepository(db);

            var retrievedA = await repo.GetByPairAsync(userId, charA);
            Assert.NotNull(retrievedA);
            Assert.Equal(charA, retrievedA.CharacterId);
            Assert.Equal(10, retrievedA.Affection);

            var retrievedB = await repo.GetByPairAsync(userId, charB);
            Assert.NotNull(retrievedB);
            Assert.Equal(charB, retrievedB.CharacterId);
            Assert.Equal(80, retrievedB.Affection);

            // Character A repo query for non-existent partner returns null
            var nonExistent = await repo.GetByPairAsync(Guid.NewGuid(), charA);
            Assert.Null(nonExistent);
        }
    }

    [Fact]
    public async Task CharacterPersonality_Isolation_CharacterACannotAccessOrMutateCharacterBPersonality()
    {
        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();

        await using (var db = new CoreDbContext(_options))
        {
            var pA = new CharacterPersonality(charA, warmth: 90, openness: 80);
            var pB = new CharacterPersonality(charB, warmth: 20, openness: 30);

            db.CharacterPersonalities.AddRange(pA, pB);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterPersonalityRepository(db);

            var loadedA = await repo.GetByCharacterIdAsync(charA);
            var loadedB = await repo.GetByCharacterIdAsync(charB);

            Assert.NotNull(loadedA);
            Assert.NotNull(loadedB);

            Assert.Equal(90, loadedA.Warmth);
            Assert.Equal(20, loadedB.Warmth);

            // Modifying A does not alter B
            loadedA.AdaptTrait(PersonalityTraitKeys.Warmth, -1);
            await db.SaveChangesAsync();

            var reloadedB = await repo.GetByCharacterIdAsync(charB);
            Assert.NotNull(reloadedB);
            Assert.Equal(20, reloadedB.Warmth);
        }
    }

    [Fact]
    public async Task CharacterSocialPresence_Isolation_CharacterACannotAccessCharacterBPresence()
    {
        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(db);

            var presenceA = CharacterSocialPresence.CreateDefault(charA, now);
            presenceA.UpdateActivity(LifeActivityType.Work, now);
            await repo.TryCreateAsync(presenceA);

            var presenceB = CharacterSocialPresence.CreateDefault(charB, now);
            presenceB.UpdateActivity(LifeActivityType.Rest, now);
            await repo.TryCreateAsync(presenceB);
        }

        await using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(db);

            var loadedA = await repo.GetByCharacterIdAsync(charA);
            var loadedB = await repo.GetByCharacterIdAsync(charB);

            Assert.NotNull(loadedA);
            Assert.NotNull(loadedB);

            Assert.Equal(LifeActivityType.Work, loadedA.CurrentActivityType);
            Assert.Equal(LifeActivityType.Rest, loadedB.CurrentActivityType);
        }
    }

    [Fact]
    public async Task AutonomousTick_Isolation_TicksAreScopedToCharacterId()
    {
        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();
        var tickId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterAutonomousLifeTickRepository(db);

            // Claim tick for Character A
            var claimA = CharacterAutonomousLifeTick.CreateClaim(charA, tickId, now, now);
            var (isClaimedA, authoritativeA) = await repo.TryClaimAsync(claimA);
            Assert.True(isClaimedA);

            // Claim tick for Character B with DIFFERENT tickId succeeds
            var tickIdB = Guid.NewGuid();
            var claimB = CharacterAutonomousLifeTick.CreateClaim(charB, tickIdB, now, now);
            var (isClaimedB, authoritativeB) = await repo.TryClaimAsync(claimB);
            Assert.True(isClaimedB);

            // Querying CharA's tick by CharB's id must return null
            var crossQuery = await repo.GetByTickIdAsync(charB, tickId);
            Assert.Null(crossQuery);

            var queryA = await repo.GetByTickIdAsync(charA, tickId);
            Assert.NotNull(queryA);
            Assert.Equal(charA, queryA.CharacterId);
        }
    }

    [Fact]
    public async Task CognitiveCycle_CrossCharacterEventInjection_IsRejected()
    {
        await using var db = new CoreDbContext(_options);

        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
        var execService = new CharacterActionExecutionService(transitionService, new CharacterActionExecutionPolicy(), NullLogger<CharacterActionExecutionService>.Instance);
        var safetyGate = new ActionSafetyGate(stateService, Array.Empty<IActionSafetyPolicy>(), NullLogger<ActionSafetyGate>.Instance);

        var cycleService = new CharacterCognitiveCycleService(
            stateService: stateService,
            experiencePolicy: new CharacterInternalExperiencePolicy(),
            appraisalPolicy: new CharacterAppraisalPolicy(),
            emotionPolicy: new CharacterEmotionPolicy(),
            desirePolicy: new CharacterDesirePolicy(),
            intentPolicy: new CharacterIntentPolicy(),
            actionProposalPolicy: new CharacterActionProposalPolicy(),
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance,
            safetyGate: safetyGate);

        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Context is for Character A, but Event is for Character B
        var crossEvent = new UserMessageCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: charB, // DIFFERENT CHARACTER
            OccurredAtUtc: now,
            Source: "User1",
            Message: "Hello Char B");

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charA,
            TriggeredAtUtc: now,
            Event: crossEvent);

        var result = await cycleService.RunAsync(context);

        // MUST fail validation with InvalidInput: Event CharacterId does not match context CharacterId
        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, result.Status);
        Assert.Contains("does not match", result.Message);
    }
}
