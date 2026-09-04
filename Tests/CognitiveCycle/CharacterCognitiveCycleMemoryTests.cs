using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.CognitiveCycle;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CognitiveCycle;

public sealed class CharacterCognitiveCycleMemoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow =
        new(2026, 9, 4, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedOccurredAt =
        new(2026, 9, 4, 14, 55, 0, TimeSpan.Zero);

    public CharacterCognitiveCycleMemoryTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private async Task<Guid> SeedCharacterStateAsync(
        decimal hunger = 80m,
        decimal energy = 80m,
        decimal stress = 20m,
        decimal socialNeed = 50m,
        decimal comfort = 50m,
        int version = 1)
    {
        var charId = Guid.NewGuid();
        await using var db = new CoreDbContext(_options);
        var state = new CharacterState(
            charId,
            initializedAtUtc: DateTime.UtcNow,
            hunger: hunger,
            energy: energy,
            stress: stress,
            socialNeed: socialNeed,
            comfort: comfort
        );

        for (int v = 1; v < version; v++)
        {
            state.ApplyDelta(CharacterStateDelta.Zero);
        }

        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();
        return charId;
    }

    private async Task SeedMemoriesAsync(Guid characterId, params (string Content, int Importance, DateTime CreatedAt)[] memories)
    {
        await using var db = new CoreDbContext(_options);
        foreach (var (content, importance, createdAt) in memories)
        {
            var memory = CharacterMemory.Create(
                characterId: characterId,
                userId: characterId,
                content: content,
                type: MemoryType.Fact,
                importance: importance,
                confidence: 1.0m
            );
            memory.SetCreated(createdAt);
            db.CharacterMemories.Add(memory);
        }
        await db.SaveChangesAsync();
    }

    private CharacterCognitiveCycleService CreateService(
        CoreDbContext db,
        ICharacterActionExecutionService? actionExecutionService = null,
        ICharacterMemoryRetrievalService? memoryRetrievalService = null,
        ICharacterMemoryFeedbackService? memoryFeedbackService = null,
        ICharacterActionProposalPolicy? actionProposalPolicy = null,
        ICharacterIntentPolicy? intentPolicy = null)
    {
        var transitionService = new CharacterStateTransitionService(
            db,
            NullLogger<CharacterStateTransitionService>.Instance);

        var stateEvolutionPolicy = new CharacterStateEvolutionPolicy();
        var stateService = new CharacterStateService(
            db,
            transitionService,
            stateEvolutionPolicy,
            NullLogger<CharacterStateService>.Instance);

        var execPolicy = new CharacterActionExecutionPolicy();
        var execService = actionExecutionService ?? new CharacterActionExecutionService(
            transitionService,
            execPolicy,
            NullLogger<CharacterActionExecutionService>.Instance);

        var retrievalService = memoryRetrievalService ?? new CharacterMemoryRetrievalService(
            db,
            NullLogger<CharacterMemoryRetrievalService>.Instance);

        var feedbackService = memoryFeedbackService ?? new CharacterMemoryFeedbackService(
            db,
            NullLogger<CharacterMemoryFeedbackService>.Instance);

        return new CharacterCognitiveCycleService(
            stateService: stateService,
            experiencePolicy: new CharacterInternalExperiencePolicy(),
            appraisalPolicy: new CharacterAppraisalPolicy(),
            emotionPolicy: new CharacterEmotionPolicy(),
            desirePolicy: new CharacterDesirePolicy(),
            intentPolicy: intentPolicy ?? new CharacterIntentPolicy(),
            actionProposalPolicy: actionProposalPolicy ?? new CharacterActionProposalPolicy(),
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance,
            memoryRetrievalService: retrievalService,
            memoryFeedbackService: feedbackService
        );
    }

    private static CharacterCognitiveCycleContext CreateContext(
        Guid characterId,
        Guid? cycleId = null,
        Guid? executionId = null,
        DateTimeOffset? triggeredAtUtc = null,
        CharacterCognitiveEvent? cognitiveEvent = null,
        CharacterPerceptionContext? perceptionContext = null)
    {
        return new CharacterCognitiveCycleContext(
            CycleId: cycleId ?? Guid.NewGuid(),
            ExecutionId: executionId ?? Guid.NewGuid(),
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc ?? FixedNow,
            Event: cognitiveEvent,
            PerceptionContext: perceptionContext
        );
    }

    #region 1. Memory Retrieval Tests

    [Fact]
    public async Task RunAsync_WithRelevantMemories_RetrievesAndMapsMemoriesDeterministically()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var t0 = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);

        await SeedMemoriesAsync(charId,
            ("Low importance memory", 2, t2),
            ("High importance older memory", 5, t0),
            ("High importance newer memory", 5, t1)
        );

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId);

        var result = await service.RunAsync(context);

        Assert.NotNull(result);
        Assert.NotNull(result.MemoryContext);
        Assert.Equal(3, result.MemoryContext.RelevantMemories.Count);

        // Deterministic ordering: Importance DESC, then CreatedAt DESC
        var first = result.MemoryContext.RelevantMemories[0];
        var second = result.MemoryContext.RelevantMemories[1];
        var third = result.MemoryContext.RelevantMemories[2];

        Assert.Equal(5, first.Importance);
        Assert.Equal("High importance newer memory", first.Content);
        Assert.Equal(5, second.Importance);
        Assert.Equal("High importance older memory", second.Content);
        Assert.Equal(2, third.Importance);
        Assert.Equal("Low importance memory", third.Content);
    }

    [Fact]
    public async Task RunAsync_WhenNoMemoriesExist_CompletesSuccessfullyWithEmptyMemoryContext()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId);

        var result = await service.RunAsync(context);

        Assert.NotNull(result);
        Assert.NotNull(result.MemoryContext);
        Assert.Empty(result.MemoryContext.RelevantMemories);
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
    }

    [Fact]
    public async Task RunAsync_WhenMemoryRetrievalThrows_DegradesGracefullyAndCompletes()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);

        var throwingRetrievalService = new ThrowingMemoryRetrievalService();

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db, memoryRetrievalService: throwingRetrievalService);
        var context = CreateContext(charId);

        var result = await service.RunAsync(context);

        // Invariant: Retrieval failure degrades to empty memory context and does NOT fail cycle
        Assert.NotNull(result);
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.NotNull(result.MemoryContext);
        Assert.Empty(result.MemoryContext.RelevantMemories);
    }

    [Fact]
    public async Task RunAsync_WhenCallerInjectsMemoryContextViaPerceptionContext_ReturnsInvalidInput()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var injectedMemory = new CharacterMemoryItem(Guid.NewGuid(), MemoryType.Fact, "Fake injected memory", 5, FixedNow);
        var injectedContext = new CharacterMemoryContext(new[] { injectedMemory });

        var perceptionWithMemory = new CharacterPerceptionContext(
            EvaluatedAtUtc: FixedNow.UtcDateTime,
            CharacterId: charId,
            MemoryContext: injectedContext
        );

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId, perceptionContext: perceptionWithMemory);

        var result = await service.RunAsync(context);

        // Invariant: Caller cannot inject MemoryContext to bypass authoritative memory retrieval
        Assert.NotNull(result);
        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, result.Status);
        Assert.Contains("PerceptionContext.MemoryContext cannot be pre-populated by caller", result.Message);
    }

    [Fact]
    public void CharacterMemoryContext_IsTrulyImmutable_UnderlyingCollectionCannotBeMutated()
    {
        var list = new List<CharacterMemoryItem>
        {
            new CharacterMemoryItem(Guid.NewGuid(), MemoryType.Fact, "Initial memory", 5, FixedNow)
        };

        var context = new CharacterMemoryContext(list);
        Assert.Single(context.RelevantMemories);

        // Mutating original list does not mutate context's RelevantMemories (defensive copy)
        list.Add(new CharacterMemoryItem(Guid.NewGuid(), MemoryType.Fact, "Mutated memory", 3, FixedNow));
        Assert.Single(context.RelevantMemories);
    }

    [Fact]
    public void CharacterCognitiveCycleContext_DoesNotExposeMemoryContextProperty()
    {
        // Invariant: MemoryContext is not a caller-accessible property on CharacterCognitiveCycleContext
        var property = typeof(CharacterCognitiveCycleContext).GetProperty("MemoryContext");
        Assert.Null(property);
    }

    #endregion

    #region 2. State Authority Invariant (P0 Regression Guard)

    [Fact]
    public async Task RunAsync_StateAuthority_DeceptiveMemoryCannotOverrideAuthoritativeDatabaseState()
    {
        // Authoritative DB state: Hunger = 20 (well-fed), Energy = 80, Version = 5
        var charId = await SeedCharacterStateAsync(
            hunger: 20m, energy: 80m, stress: 10m, socialNeed: 30m, comfort: 80m, version: 5);

        // Memory states that the character is starving
        await SeedMemoriesAsync(charId,
            ("Character was starving yesterday. Hunger was 95.", 5, DateTime.UtcNow.AddDays(-1)),
            ("Character skipped lunch and dinner.", 4, DateTime.UtcNow.AddHours(-12))
        );

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId);

        var result = await service.RunAsync(context);

        // Invariant: Experience reflects authoritative DB Hunger (20m), NOT the deceptive memory
        Assert.NotNull(result);
        Assert.Equal(5, result.StateVersionAtStart);
        Assert.NotNull(result.Experience);
        Assert.Equal(20m, result.Experience.Hunger.RawValue);

        // Hunger was satisfied at 20, so Eat proposal cannot be formed
        Assert.True(result.ActionProposal == null || result.ActionProposal.Proposal == null || result.ActionProposal.Proposal.Type != ActionType.Eat);

        // Authoritative DB state remains unchanged at Hunger = 20
        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(20m, state.Hunger);
    }

    #endregion

    #region 3. Memory Feedback Tests

    [Fact]
    public async Task RunAsync_WhenActionExecuted_CreatesActionCompletedMemoryFeedback()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var executionId = Guid.NewGuid();

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId, executionId: executionId);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.NotNull(result.MemoryFeedback);
        Assert.Equal(CharacterMemoryFeedbackType.ActionCompleted, result.MemoryFeedback.Type);
        Assert.Equal(executionId, result.MemoryFeedback.ExecutionId);
        Assert.Contains("Performed action", result.MemoryFeedback.Content);

        // Verify row in DB
        await using var verifyDb = new CoreDbContext(_options);
        var memoryInDb = await verifyDb.CharacterMemories.FirstOrDefaultAsync(m => m.Id == result.MemoryFeedback.MemoryId);
        Assert.NotNull(memoryInDb);
        Assert.Equal(charId, memoryInDb.CharacterId);
        Assert.Contains("Performed action", memoryInDb.Content);
    }

    [Fact]
    public async Task RunAsync_WhenActionExecutionFails_CreatesActionFailedMemoryFeedback()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var failingExecService = new FailingActionExecutionService();

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db, actionExecutionService: failingExecService);
        var context = CreateContext(charId);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.ConcurrencyConflict, result.Status);
        Assert.NotNull(result.MemoryFeedback);
        Assert.Equal(CharacterMemoryFeedbackType.ActionFailed, result.MemoryFeedback.Type);
        Assert.Contains("execution failed", result.MemoryFeedback.Content);
    }

    [Fact]
    public async Task RunAsync_WhenNoActionProposed_WithEvent_CreatesEventExperiencedMemoryFeedback()
    {
        // Low hunger, high energy -> no strong drive for action
        var charId = await SeedCharacterStateAsync(hunger: 10m, energy: 90m, stress: 5m, socialNeed: 10m, comfort: 90m);
        var userEvent = new UserMessageCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: charId,
            OccurredAtUtc: FixedOccurredAt,
            Message: "Nice weather today!",
            Source: "Friend"
        );

        var noActionProposalPolicy = new NullActionProposalPolicy();

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db, actionProposalPolicy: noActionProposalPolicy);
        var context = CreateContext(charId, cognitiveEvent: userEvent);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithoutAction, result.Status);
        Assert.NotNull(result.MemoryFeedback);
        Assert.Equal(CharacterMemoryFeedbackType.EventExperienced, result.MemoryFeedback.Type);
        Assert.Contains("Experienced UserMessage from Friend", result.MemoryFeedback.Content);
    }

    [Fact]
    public async Task RunAsync_SameExecutionId_WithSameSemanticPayload_IsIdempotent_ReusesFeedback()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();
        var sharedCycleId = Guid.NewGuid();

        Guid firstMemoryId;
        await using (var db = new CoreDbContext(_options))
        {
            var service = CreateService(db);
            var context = CreateContext(charId, cycleId: sharedCycleId, executionId: sharedExecutionId);
            var result = await service.RunAsync(context);

            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
            Assert.NotNull(result.MemoryFeedback);
            firstMemoryId = result.MemoryFeedback.MemoryId;
        }

        // Retry with same ExecutionId simulating AlreadyExecuted response (idempotent replay)
        await using (var db = new CoreDbContext(_options))
        {
            var alreadyExecutedService = new AlreadyExecutedActionExecutionService();
            var service = CreateService(db, actionExecutionService: alreadyExecutedService);
            var context = CreateContext(charId, cycleId: sharedCycleId, executionId: sharedExecutionId);
            var result = await service.RunAsync(context);

            Assert.Equal(CharacterCognitiveCycleStatus.AlreadyExecuted, result.Status);
            Assert.NotNull(result.MemoryFeedback);
            Assert.Equal(firstMemoryId, result.MemoryFeedback.MemoryId);
            Assert.Equal(CharacterMemoryFeedbackType.ActionCompleted, result.MemoryFeedback.Type);
        }

        // Assert: Exactly 1 row in CharacterMemories for this execution
        await using var verifyDb = new CoreDbContext(_options);
        var count = await verifyDb.CharacterMemories.CountAsync(m => m.SourceSessionId == sharedExecutionId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RunAsync_SameExecutionId_WithDifferentSemanticOutcome_ReturnsIdempotencyConflict()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();
        var cycleId1 = Guid.NewGuid();
        var cycleId2 = Guid.NewGuid();

        // Run 1: Succeeded with Eat
        await using (var db1 = new CoreDbContext(_options))
        {
            var service1 = CreateService(db1);
            var context1 = CreateContext(charId, cycleId: cycleId1, executionId: sharedExecutionId);
            var result1 = await service1.RunAsync(context1);

            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result1.Status);
            Assert.NotNull(result1.MemoryFeedback);
        }

        // Run 2: Same ExecutionId, but different semantic outcome (FailingActionExecutionService)
        await using (var db2 = new CoreDbContext(_options))
        {
            var failingExecService = new FailingActionExecutionService();
            var service2 = CreateService(db2, actionExecutionService: failingExecService);
            var context2 = CreateContext(charId, cycleId: cycleId2, executionId: sharedExecutionId);
            var result2 = await service2.RunAsync(context2);

            // Invariant: Same ExecutionId with conflicting semantic feedback produces IdempotencyConflict!
            Assert.Equal(CharacterCognitiveCycleStatus.IdempotencyConflict, result2.Status);
            Assert.Contains("already been processed with a different semantic feedback payload", result2.Message);
        }

        // Invariant: Database still has exactly 1 memory row (from Run 1)
        await using var verifyDb = new CoreDbContext(_options);
        var count = await verifyDb.CharacterMemories.CountAsync(m => m.SourceSessionId == sharedExecutionId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RunAsync_ExistingFeedback_TypeMatchesVerifiedSemanticPayload_NotStringParsing()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId, executionId: sharedExecutionId);
        var res = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, res.Status);
        Assert.NotNull(res.MemoryFeedback);
        // Type is ActionCompleted from semantic payload, not parsed from Content string prefix
        Assert.Equal(CharacterMemoryFeedbackType.ActionCompleted, res.MemoryFeedback.Type);
    }

    [Fact]
    public void CanonicalFeedbackFingerprint_DifferentSemanticPayloads_ProduceDistinctFingerprints()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        var fp1 = CanonicalFeedbackFingerprint.Compute(charId, execId, CharacterMemoryFeedbackType.ActionCompleted, "Performed action Eat: HungerDriven.");
        var fp2 = CanonicalFeedbackFingerprint.Compute(charId, execId, CharacterMemoryFeedbackType.ActionFailed, "Performed action Eat: HungerDriven.");
        var fp3 = CanonicalFeedbackFingerprint.Compute(charId, execId, CharacterMemoryFeedbackType.ActionCompleted, "Performed action Sleep: Tired.");
        var fp4 = CanonicalFeedbackFingerprint.Compute(Guid.NewGuid(), execId, CharacterMemoryFeedbackType.ActionCompleted, "Performed action Eat: HungerDriven.");

        Assert.NotEqual(fp1, fp2);
        Assert.NotEqual(fp1, fp3);
        Assert.NotEqual(fp1, fp4);
    }

    [Fact]
    public async Task RunAsync_ReplayReturnsFeedbackRepresentingPersistedSemantics_RecoveredFromEntity()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();

        // Initial run creates ActionCompleted memory with independent salience (default Importance = 3)
        await using (var db1 = new CoreDbContext(_options))
        {
            var service = CreateService(db1);
            var context = CreateContext(charId, executionId: sharedExecutionId);
            var res = await service.RunAsync(context);
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, res.Status);
        }

        // Verify DB directly
        await using (var verifyDb = new CoreDbContext(_options))
        {
            var memory = await verifyDb.CharacterMemories.SingleAsync(m => m.SourceSessionId == sharedExecutionId);
            // Invariant: Importance is independent memory salience (default 3), NOT coupled to FeedbackType!
            Assert.Equal(3, memory.Importance);
            Assert.Equal(CharacterMemoryFeedbackType.ActionCompleted, memory.FeedbackType);
            Assert.NotNull(memory.FeedbackFingerprint);

            // Replay with AlreadyExecuted
            var alreadyExecutedService = new AlreadyExecutedActionExecutionService();
            var service = CreateService(verifyDb, actionExecutionService: alreadyExecutedService);
            var context = CreateContext(charId, executionId: sharedExecutionId);
            var replayResult = await service.RunAsync(context);

            Assert.Equal(CharacterCognitiveCycleStatus.AlreadyExecuted, replayResult.Status);
            Assert.NotNull(replayResult.MemoryFeedback);
            // Invariant: Type is recovered from persisted entity (FeedbackType column), not Importance or Content parsing
            Assert.Equal(CharacterMemoryFeedbackType.ActionCompleted, replayResult.MemoryFeedback.Type);
            Assert.Equal(memory.Id, replayResult.MemoryFeedback.MemoryId);
        }
    }

    [Fact]
    public async Task Replay_ActionFailed_WithImportance5_RecoversActionFailed_IndependentOfImportance()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();
        var memoryId = DeterministicMemoryIdHelper(charId, sharedExecutionId);
        var canonicalContent = "Attempted action Eat but execution failed: Simulated execution failure..";
        var fp = CanonicalFeedbackFingerprint.Compute(charId, sharedExecutionId, CharacterMemoryFeedbackType.ActionFailed, canonicalContent);

        // Seed DB directly with ActionFailed and Importance = 5 (e.g. Critical high-salience failure)
        await using (var seedDb = new CoreDbContext(_options))
        {
            var memory = CharacterMemory.Create(
                characterId: charId,
                userId: charId,
                content: canonicalContent,
                type: MemoryType.Event,
                importance: 5, // High salience (5)
                confidence: 1.0m,
                sourceSessionId: sharedExecutionId,
                feedbackType: CharacterMemoryFeedbackType.ActionFailed,
                feedbackFingerprint: fp
            );
            memory.Id = memoryId;
            await seedDb.CharacterMemories.AddAsync(memory);
            await seedDb.SaveChangesAsync();
        }

        // Run cycle with FailingActionExecutionService (produces ActionFailed)
        await using (var db = new CoreDbContext(_options))
        {
            var failingService = new FailingActionExecutionService();
            var service = CreateService(db, actionExecutionService: failingService);
            var context = CreateContext(charId, executionId: sharedExecutionId);
            var res = await service.RunAsync(context);

            // Invariant: Status is ConcurrencyConflict, and MemoryFeedback.Type is ActionFailed despite Importance being 5!
            Assert.Equal(CharacterCognitiveCycleStatus.ConcurrencyConflict, res.Status);
            Assert.NotNull(res.MemoryFeedback);
            Assert.Equal(CharacterMemoryFeedbackType.ActionFailed, res.MemoryFeedback.Type);
            Assert.Equal(memoryId, res.MemoryFeedback.MemoryId);
        }
    }

    [Fact]
    public async Task Replay_ActionCompleted_WithImportance1_RecoversActionCompleted_IndependentOfImportance()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();
        var memoryId = DeterministicMemoryIdHelper(charId, sharedExecutionId);
        var canonicalContent = "Performed action Eat: HungerDriven.";
        var fp = CanonicalFeedbackFingerprint.Compute(charId, sharedExecutionId, CharacterMemoryFeedbackType.ActionCompleted, canonicalContent);

        // Seed DB directly with ActionCompleted and Importance = 1 (e.g. Minor routine action)
        await using (var seedDb = new CoreDbContext(_options))
        {
            var memory = CharacterMemory.Create(
                characterId: charId,
                userId: charId,
                content: canonicalContent,
                type: MemoryType.Event,
                importance: 1, // Low salience (1)
                confidence: 1.0m,
                sourceSessionId: sharedExecutionId,
                feedbackType: CharacterMemoryFeedbackType.ActionCompleted,
                feedbackFingerprint: fp
            );
            memory.Id = memoryId;
            await seedDb.CharacterMemories.AddAsync(memory);
            await seedDb.SaveChangesAsync();
        }

        // Replay with AlreadyExecuted
        await using (var db = new CoreDbContext(_options))
        {
            var alreadyExecutedService = new AlreadyExecutedActionExecutionService();
            var service = CreateService(db, actionExecutionService: alreadyExecutedService);
            var context = CreateContext(charId, executionId: sharedExecutionId);
            var res = await service.RunAsync(context);

            Assert.Equal(CharacterCognitiveCycleStatus.AlreadyExecuted, res.Status);
            Assert.NotNull(res.MemoryFeedback);
            // Invariant: Type is ActionCompleted, NOT NoActionTaken (which previously was 1 in the old mapping!)
            Assert.Equal(CharacterMemoryFeedbackType.ActionCompleted, res.MemoryFeedback.Type);
            Assert.Equal(memoryId, res.MemoryFeedback.MemoryId);
        }
    }

    [Fact]
    public async Task Replay_SameExecutionId_DifferentFeedbackType_ThrowsIdempotencyConflict()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();
        var memoryId = DeterministicMemoryIdHelper(charId, sharedExecutionId);

        // Seed DB with ActionCompleted
        await using (var seedDb = new CoreDbContext(_options))
        {
            var memory = CharacterMemory.Create(
                characterId: charId,
                userId: charId,
                content: "Performed action Eat: HungerDriven.",
                type: MemoryType.Event,
                importance: 3,
                confidence: 1.0m,
                sourceSessionId: sharedExecutionId,
                feedbackType: CharacterMemoryFeedbackType.ActionCompleted,
                feedbackFingerprint: CanonicalFeedbackFingerprint.Compute(charId, sharedExecutionId, CharacterMemoryFeedbackType.ActionCompleted, "Performed action Eat: HungerDriven.")
            );
            memory.Id = memoryId;
            await seedDb.CharacterMemories.AddAsync(memory);
            await seedDb.SaveChangesAsync();
        }

        // Run cycle with FailingActionExecutionService (produces ActionFailed)
        await using (var db = new CoreDbContext(_options))
        {
            var failingService = new FailingActionExecutionService();
            var service = CreateService(db, actionExecutionService: failingService);
            var context = CreateContext(charId, executionId: sharedExecutionId);
            var res = await service.RunAsync(context);

            // Invariant: Conflict detected between ActionCompleted in DB and ActionFailed in current execution
            Assert.Equal(CharacterCognitiveCycleStatus.IdempotencyConflict, res.Status);
            Assert.Contains("different semantic feedback payload", res.Message);
        }
    }

    [Fact]
    public async Task Replay_SameExecutionId_DifferentCanonicalContent_ThrowsIdempotencyConflict()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();
        var memoryId = DeterministicMemoryIdHelper(charId, sharedExecutionId);

        // Seed DB with ActionCompleted for a DIFFERENT action ("Performed action Sleep: Tired.")
        await using (var seedDb = new CoreDbContext(_options))
        {
            var memory = CharacterMemory.Create(
                characterId: charId,
                userId: charId,
                content: "Performed action Sleep: Tired.",
                type: MemoryType.Event,
                importance: 3,
                confidence: 1.0m,
                sourceSessionId: sharedExecutionId,
                feedbackType: CharacterMemoryFeedbackType.ActionCompleted,
                feedbackFingerprint: CanonicalFeedbackFingerprint.Compute(charId, sharedExecutionId, CharacterMemoryFeedbackType.ActionCompleted, "Performed action Sleep: Tired.")
            );
            memory.Id = memoryId;
            await seedDb.CharacterMemories.AddAsync(memory);
            await seedDb.SaveChangesAsync();
        }

        // Replay with Eat cycle (hunger = 90) -> produces "Performed action Eat: HungerDriven."
        await using (var db = new CoreDbContext(_options))
        {
            var alreadyExecutedService = new AlreadyExecutedActionExecutionService();
            var service = CreateService(db, actionExecutionService: alreadyExecutedService);
            var context = CreateContext(charId, executionId: sharedExecutionId);
            var res = await service.RunAsync(context);

            // Invariant: Conflict detected between Sleep content in DB and Eat content in incoming execution
            Assert.Equal(CharacterCognitiveCycleStatus.IdempotencyConflict, res.Status);
            Assert.Contains("different semantic feedback payload", res.Message);
        }
    }

    [Fact]
    public async Task Replay_DatabaseTypeOverridesContentPrefix_NoFeedbackTypeReconstructedFromContent()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();
        var memoryId = DeterministicMemoryIdHelper(charId, sharedExecutionId);
        // Deceptive content: starts with "Performed action Eat", but DB has FeedbackType = ActionFailed!
        var deceptiveContent = "Performed action Eat: HungerDriven.";
        var fp = CanonicalFeedbackFingerprint.Compute(charId, sharedExecutionId, CharacterMemoryFeedbackType.ActionFailed, deceptiveContent);

        await using (var seedDb = new CoreDbContext(_options))
        {
            var memory = CharacterMemory.Create(
                characterId: charId,
                userId: charId,
                content: deceptiveContent,
                type: MemoryType.Event,
                importance: 4,
                confidence: 1.0m,
                sourceSessionId: sharedExecutionId,
                feedbackType: CharacterMemoryFeedbackType.ActionFailed, // In DB: ActionFailed!
                feedbackFingerprint: fp
            );
            memory.Id = memoryId;
            await seedDb.CharacterMemories.AddAsync(memory);
            await seedDb.SaveChangesAsync();
        }

        // Run cycle which would normally produce ActionCompleted with "Performed action Eat: HungerDriven."
        await using (var db = new CoreDbContext(_options))
        {
            var service = CreateService(db);
            var context = CreateContext(charId, executionId: sharedExecutionId);
            var res = await service.RunAsync(context);

            // Invariant: The system does NOT parse the Content prefix "Performed action" to claim ActionCompleted.
            // Instead, it compares semantic FeedbackType (Existing: ActionFailed != Incoming: ActionCompleted)
            // and detects an IdempotencyConflict!
            Assert.Equal(CharacterCognitiveCycleStatus.IdempotencyConflict, res.Status);
            Assert.Contains("different semantic feedback payload", res.Message);
        }
    }

    [Fact]
    public async Task RunAsync_DeterministicMemoryId_IsPureFunctionOfCharacterAndExecutionId()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var execId = Guid.NewGuid();

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId, executionId: execId);
        var res = await service.RunAsync(context);

        Assert.NotNull(res.MemoryFeedback);
        var expectedGuid = DeterministicMemoryIdHelper(charId, execId);
        Assert.Equal(expectedGuid, res.MemoryFeedback.MemoryId);
    }

    private static Guid DeterministicMemoryIdHelper(Guid characterId, Guid executionId)
    {
        var canonical = $"MemoryFeedback:{characterId:D}:{executionId:D}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    [Fact]
    public async Task RunAsync_DifferentExecutionIds_CreateIndependentMemoryFeedbacks()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var executionId1 = Guid.NewGuid();
        var executionId2 = Guid.NewGuid();

        await using (var db1 = new CoreDbContext(_options))
        {
            var service1 = CreateService(db1);
            var context1 = CreateContext(charId, executionId: executionId1);
            var res1 = await service1.RunAsync(context1);
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, res1.Status);
        }

        await using (var db2 = new CoreDbContext(_options))
        {
            var service2 = CreateService(db2);
            var context2 = CreateContext(charId, executionId: executionId2);
            var res2 = await service2.RunAsync(context2);
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, res2.Status);
        }

        await using var verifyDb = new CoreDbContext(_options);
        var count1 = await verifyDb.CharacterMemories.CountAsync(m => m.SourceSessionId == executionId1);
        var count2 = await verifyDb.CharacterMemories.CountAsync(m => m.SourceSessionId == executionId2);

        Assert.Equal(1, count1);
        Assert.Equal(1, count2);
    }

    [Fact]
    public async Task RunAsync_WhenMemoryFeedbackPersistenceFails_CommittedStateTransitionIsNotRolledBack()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m, version: 1);
        var throwingFeedbackService = new ThrowingMemoryFeedbackService();

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db, memoryFeedbackService: throwingFeedbackService);
        var context = CreateContext(charId);

        var result = await service.RunAsync(context);

        // Cycle succeeds with action
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.Null(result.MemoryFeedback); // feedback failed gracefully

        // Invariant: State transition in DB was committed from v1 -> v2 and NOT rolled back!
        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(2, state.Version);
    }

    #endregion

    #region 4. Determinism & Concurrency

    [Fact]
    public async Task RunAsync_WithSameStateAndEventAndMemories_IsDeterministic_AcrossCycles()
    {
        var charId1 = await SeedCharacterStateAsync(hunger: 85m, energy: 40m, stress: 35m, version: 3);
        var charId2 = await SeedCharacterStateAsync(hunger: 85m, energy: 40m, stress: 35m, version: 3);

        await SeedMemoriesAsync(charId1, ("Important memory", 5, FixedNow.UtcDateTime.AddDays(-1)));
        await SeedMemoriesAsync(charId2, ("Important memory", 5, FixedNow.UtcDateTime.AddDays(-1)));

        var eventId = Guid.NewGuid();
        var userEvent1 = new UserMessageCognitiveEvent(
            EventId: eventId,
            CharacterId: charId1,
            OccurredAtUtc: FixedOccurredAt,
            Message: "Deterministic test",
            Source: "Tester"
        );
        var userEvent2 = new UserMessageCognitiveEvent(
            EventId: eventId,
            CharacterId: charId2,
            OccurredAtUtc: FixedOccurredAt,
            Message: "Deterministic test",
            Source: "Tester"
        );

        var cycleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        var context1 = CreateContext(charId1, cycleId: cycleId, executionId: executionId, cognitiveEvent: userEvent1);
        var context2 = CreateContext(charId2, cycleId: cycleId, executionId: executionId, cognitiveEvent: userEvent2);

        await using var db1 = new CoreDbContext(_options);
        var service1 = CreateService(db1);
        var result1 = await service1.RunAsync(context1);

        await using var db2 = new CoreDbContext(_options);
        var service2 = CreateService(db2);
        var result2 = await service2.RunAsync(context2);

        Assert.Equal(result1.Status, result2.Status);
        Assert.Equal(result1.StateVersionAtStart, result2.StateVersionAtStart);
        Assert.Equal(result1.Experience!.DominantNeed, result2.Experience!.DominantNeed);
        Assert.Equal(result1.Appraisal!.Type, result2.Appraisal!.Type);
        Assert.Equal(result1.Emotion!.Type, result2.Emotion!.Type);
        Assert.Equal(result1.Desires!.DominantDesire?.Type, result2.Desires!.DominantDesire?.Type);
        Assert.Equal(result1.Intent!.Intent?.Type, result2.Intent!.Intent?.Type);
        Assert.Equal(result1.ActionProposal!.Proposal?.Type, result2.ActionProposal!.Proposal?.Type);
        Assert.Equal(result1.ActionExecution!.Status, result2.ActionExecution!.Status);
        Assert.Equal(result1.MemoryContext!.RelevantMemories.Count, result2.MemoryContext!.RelevantMemories.Count);
        Assert.Equal(result1.MemoryFeedback!.Type, result2.MemoryFeedback!.Type);
        Assert.Equal(result1.MemoryFeedback.Content, result2.MemoryFeedback.Content);
    }

    [Fact]
    public async Task RunAsync_TenConcurrentWorkers_WithSameExecutionId_ProducesExactlyOneStateTransitionAndOneMemoryFeedback()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();
        var sharedCycleId = Guid.NewGuid();
        var sharedEventId = Guid.NewGuid();

        var sharedEvent = new WorldCognitiveEvent(
            EventId: sharedEventId,
            CharacterId: charId,
            OccurredAtUtc: FixedOccurredAt,
            EventName: "Thunderstorm",
            Source: "World"
        );

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var service = CreateService(workerDb);
            var context = CreateContext(charId, cycleId: sharedCycleId, executionId: sharedExecutionId, cognitiveEvent: sharedEvent);
            return await service.RunAsync(context);
        });

        var results = await Task.WhenAll(tasks);

        var appliedCount = results.Count(r => r.Status == CharacterCognitiveCycleStatus.CompletedWithAction);
        var nonAppliedCount = results.Count(r => r.Status != CharacterCognitiveCycleStatus.CompletedWithAction);

        Assert.Equal(1, appliedCount);
        Assert.Equal(9, nonAppliedCount);

        // Invariant: Exactly 1 state transition recorded in DB
        await using var verifyDb = new CoreDbContext(_options);
        var transitions = await verifyDb.CharacterStateTransitions.Where(t => t.CharacterId == charId).ToListAsync();
        Assert.Single(transitions);

        // Invariant: Exactly 1 memory feedback recorded in DB
        var memories = await verifyDb.CharacterMemories.Where(m => m.SourceSessionId == sharedExecutionId).ToListAsync();
        Assert.Single(memories);
    }

    #endregion

    #region Helper Test Doubles

    private sealed class ThrowingMemoryRetrievalService : ICharacterMemoryRetrievalService
    {
        public Task<CharacterMemoryContext> RetrieveRelevantAsync(
            Guid characterId,
            CharacterPerceptionContext perceptionContext,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated memory retrieval database failure.");
        }
    }

    private sealed class ThrowingMemoryFeedbackService : ICharacterMemoryFeedbackService
    {
        public Task<CharacterMemoryFeedback?> RecordFeedbackAsync(
            CharacterCognitiveCycleContext cycleContext,
            CharacterCognitiveCycleResult cycleResult,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated memory feedback database crash.");
        }

        public Task<CharacterMemoryFeedback?> RecordFeedbackAsync(
            CharacterCognitiveCycleContext cycleContext,
            CharacterCognitiveCycleResult cycleResult,
            int importance,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated memory feedback database crash.");
        }
    }

    private sealed class FailingActionExecutionService : ICharacterActionExecutionService
    {
        public Task<CharacterActionExecutionResult> ExecuteAsync(
            Guid characterId,
            CharacterActionProposal proposal,
            CharacterActionExecutionContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult(CharacterActionExecutionResult.ConcurrencyConflict(
                context.ExecutionId, characterId, proposal, 1, "Simulated execution failure."));
        }
    }

    private sealed class NullActionProposalPolicy : ICharacterActionProposalPolicy
    {
        public CharacterActionProposalEvaluation Evaluate(
            CharacterIntentEvaluation intent,
            CharacterActionProposalContext context)
        {
            return new CharacterActionProposalEvaluation(
                characterId: intent.CharacterId,
                stateVersion: intent.StateVersion,
                proposal: null,
                evaluatedAtUtc: context.EvaluatedAtUtc
            );
        }
    }

    private sealed class AlreadyExecutedActionExecutionService : ICharacterActionExecutionService
    {
        public Task<CharacterActionExecutionResult> ExecuteAsync(
            Guid characterId,
            CharacterActionProposal proposal,
            CharacterActionExecutionContext context,
            CancellationToken ct = default)
        {
            var snapshot = new CharacterStateSnapshot(
                energy: 80, hunger: 20, version: 2);

            return Task.FromResult(CharacterActionExecutionResult.AlreadyExecuted(
                context.ExecutionId, characterId, proposal, 1, 2, CharacterStateDelta.Zero, snapshot));
        }
    }

    #endregion
}
