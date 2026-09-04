using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.CognitiveCycle;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CognitiveCycle;

public sealed class CharacterCognitiveCycleRelationshipTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow =
        new(2026, 9, 4, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedOccurredAt =
        new(2026, 9, 4, 15, 55, 0, TimeSpan.Zero);

    public CharacterCognitiveCycleRelationshipTests()
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

    #region Helper Setup Methods

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

    private async Task<CharacterRelationship> SeedRelationshipAsync(
        Guid characterId,
        RelationshipTargetType targetType,
        Guid targetId,
        RelationshipType relationshipType = RelationshipType.Stranger,
        int trust = 0,
        int affection = 0,
        int familiarity = 0)
    {
        await using var db = new CoreDbContext(_options);
        var rel = CharacterRelationship.Create(
            characterId: characterId,
            targetType: targetType,
            targetId: targetId,
            relationshipType: relationshipType,
            trust: trust,
            affection: affection,
            familiarity: familiarity
        );
        db.CharacterRelationships.Add(rel);
        await db.SaveChangesAsync();
        return rel;
    }

    private CharacterCognitiveCycleService CreateService(
        CoreDbContext db,
        ICharacterActionExecutionService? actionExecutionService = null,
        ICharacterMemoryRetrievalService? memoryRetrievalService = null,
        ICharacterMemoryFeedbackService? memoryFeedbackService = null,
        ICharacterRelationshipRetrievalService? relationshipRetrievalService = null,
        ICharacterRelationshipFeedbackService? relationshipFeedbackService = null,
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

        var memoryRetrieval = memoryRetrievalService ?? new CharacterMemoryRetrievalService(
            db,
            NullLogger<CharacterMemoryRetrievalService>.Instance);

        var memoryFeedback = memoryFeedbackService ?? new CharacterMemoryFeedbackService(
            db,
            NullLogger<CharacterMemoryFeedbackService>.Instance);

        var relRepo = new CharacterRelationshipRepository(db);
        var relRetrieval = relationshipRetrievalService ?? new CharacterRelationshipRetrievalService(
            relRepo,
            NullLogger<CharacterRelationshipRetrievalService>.Instance);

        var relTransition = new CharacterRelationshipTransitionService(
            db,
            NullLogger<CharacterRelationshipTransitionService>.Instance);

        var relFeedback = relationshipFeedbackService ?? new CharacterRelationshipFeedbackService(
            relTransition,
            new DefaultCharacterRelationshipFeedbackPolicy(),
            NullLogger<CharacterRelationshipFeedbackService>.Instance);

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
            memoryRetrievalService: memoryRetrieval,
            memoryFeedbackService: memoryFeedback,
            relationshipRetrievalService: relRetrieval,
            relationshipFeedbackService: relFeedback
        );
    }

    private static CharacterCognitiveCycleContext CreateContext(
        Guid characterId,
        Guid? cycleId = null,
        Guid? executionId = null,
        CharacterCognitiveEvent? cognitiveEvent = null,
        CharacterRelationshipContext? relationshipContext = null,
        CharacterPerceptionContext? perceptionContext = null)
    {
        return new CharacterCognitiveCycleContext(
            CycleId: cycleId ?? Guid.NewGuid(),
            ExecutionId: executionId ?? Guid.NewGuid(),
            CharacterId: characterId,
            TriggeredAtUtc: FixedNow,
            Event: cognitiveEvent,
            PerceptionContext: perceptionContext,
            RelationshipContext: relationshipContext
        );
    }

    #endregion

    #region 1. Domain Tests

    [Fact]
    public void Create_InitialRelationship_HasCorrectDefaults()
    {
        var charId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var rel = CharacterRelationship.Create(charId, RelationshipTargetType.User, targetId);

        Assert.Equal(charId, rel.CharacterId);
        Assert.Equal(RelationshipTargetType.User, rel.TargetType);
        Assert.Equal(targetId, rel.TargetId);
        Assert.Equal(RelationshipType.Stranger, rel.RelationshipType);
        Assert.Equal(0, rel.Trust);
        Assert.Equal(0, rel.Affection);
        Assert.Equal(0, rel.Familiarity);
        Assert.Equal(1u, rel.Version);
    }

    [Fact]
    public void ApplyTrustDelta_ClampsWithinZeroAndOneHundred()
    {
        var rel = CharacterRelationship.Create(Guid.NewGuid(), RelationshipTargetType.User, Guid.NewGuid());

        // Underflow clamp to 0
        var (_, new1, actual1) = rel.ApplyTrustDelta(-50);
        Assert.Equal(0, new1);
        Assert.Equal(0, actual1);

        // Increase to 70
        var (_, new2, actual2) = rel.ApplyTrustDelta(70);
        Assert.Equal(70, new2);
        Assert.Equal(70, actual2);

        // Overflow clamp to 100
        var (_, new3, actual3) = rel.ApplyTrustDelta(50);
        Assert.Equal(100, new3);
        Assert.Equal(30, actual3);
    }

    [Fact]
    public void ApplyAffectionDelta_ClampsWithinZeroAndOneHundred()
    {
        var rel = CharacterRelationship.Create(Guid.NewGuid(), RelationshipTargetType.User, Guid.NewGuid());

        rel.ApplyAffectionDelta(-20);
        Assert.Equal(0, rel.Affection);

        rel.ApplyAffectionDelta(60);
        Assert.Equal(60, rel.Affection);

        rel.ApplyAffectionDelta(80);
        Assert.Equal(100, rel.Affection);
    }

    [Fact]
    public void ApplyFamiliarityDelta_ClampsWithinZeroAndOneHundred()
    {
        var rel = CharacterRelationship.Create(Guid.NewGuid(), RelationshipTargetType.User, Guid.NewGuid());

        rel.ApplyFamiliarityDelta(-10);
        Assert.Equal(0, rel.Familiarity);

        rel.ApplyFamiliarityDelta(45);
        Assert.Equal(45, rel.Familiarity);

        rel.ApplyFamiliarityDelta(90);
        Assert.Equal(100, rel.Familiarity);
    }

    [Fact]
    public void ChangeRelationshipType_UpdatesTypeAndIncrementsVersion()
    {
        var rel = CharacterRelationship.Create(Guid.NewGuid(), RelationshipTargetType.User, Guid.NewGuid());
        var vBefore = rel.Version;

        rel.ChangeRelationshipType(RelationshipType.Friend);

        Assert.Equal(RelationshipType.Friend, rel.RelationshipType);
        Assert.True(rel.Version > vBefore);
    }

    #endregion

    #region 2. Retrieval Tests

    [Fact]
    public async Task RetrieveRelationshipAsync_WhenExists_ReturnsAuthoritativeContext()
    {
        var charId = await SeedCharacterStateAsync();
        var targetId = Guid.NewGuid();
        await SeedRelationshipAsync(charId, RelationshipTargetType.User, targetId, RelationshipType.Acquaintance, trust: 30, affection: 40, familiarity: 50);

        await using var db = new CoreDbContext(_options);
        var repo = new CharacterRelationshipRepository(db);
        var retrievalService = new CharacterRelationshipRetrievalService(repo, NullLogger<CharacterRelationshipRetrievalService>.Instance);

        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Hi!", UserId: targetId);
        var context = await retrievalService.RetrieveRelationshipAsync(charId, userEvent);

        Assert.NotNull(context);
        Assert.Equal(targetId, context.TargetId);
        Assert.Equal(RelationshipTargetType.User, context.TargetType);
        Assert.Equal(RelationshipType.Acquaintance, context.RelationshipType);
        Assert.Equal(30, context.Trust);
        Assert.Equal(40, context.Affection);
        Assert.Equal(50, context.Familiarity);
    }

    [Fact]
    public async Task RetrieveRelationshipAsync_WhenMissing_CreatesInitialStrangerRelationship()
    {
        var charId = await SeedCharacterStateAsync();
        var targetId = Guid.NewGuid();

        await using var db = new CoreDbContext(_options);
        var repo = new CharacterRelationshipRepository(db);
        var retrievalService = new CharacterRelationshipRetrievalService(repo, NullLogger<CharacterRelationshipRetrievalService>.Instance);

        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "First time meeting!", UserId: targetId);
        var context = await retrievalService.RetrieveRelationshipAsync(charId, userEvent);

        Assert.NotNull(context);
        Assert.Equal(targetId, context.TargetId);
        Assert.Equal(RelationshipType.Stranger, context.RelationshipType);
        Assert.Equal(0, context.Trust);
        Assert.Equal(0, context.Affection);
        Assert.Equal(0, context.Familiarity);

        // Verify persisted in DB
        await using var verifyDb = new CoreDbContext(_options);
        var persisted = await verifyDb.CharacterRelationships.FirstOrDefaultAsync(r => r.CharacterId == charId && r.TargetId == targetId);
        Assert.NotNull(persisted);
        Assert.Equal(RelationshipType.Stranger, persisted.RelationshipType);
    }

    [Fact]
    public async Task RetrieveRelationshipAsync_DifferentTargetIds_RemainStrictlyIsolated()
    {
        var charId = await SeedCharacterStateAsync();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await SeedRelationshipAsync(charId, RelationshipTargetType.User, userA, RelationshipType.Friend, trust: 80, affection: 70, familiarity: 90);
        await SeedRelationshipAsync(charId, RelationshipTargetType.User, userB, RelationshipType.Stranger, trust: 5, affection: 0, familiarity: 10);

        await using var db = new CoreDbContext(_options);
        var repo = new CharacterRelationshipRepository(db);
        var retrievalService = new CharacterRelationshipRetrievalService(repo, NullLogger<CharacterRelationshipRetrievalService>.Instance);

        var ctxA = await retrievalService.RetrieveRelationshipAsync(charId, new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Hey", UserId: userA));
        var ctxB = await retrievalService.RetrieveRelationshipAsync(charId, new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Who are you?", UserId: userB));

        Assert.NotNull(ctxA);
        Assert.NotNull(ctxB);
        Assert.Equal(RelationshipType.Friend, ctxA.RelationshipType);
        Assert.Equal(80, ctxA.Trust);
        Assert.Equal(RelationshipType.Stranger, ctxB.RelationshipType);
        Assert.Equal(5, ctxB.Trust);
    }

    [Fact]
    public async Task RunAsync_WhenCallerInjectsConflictingRelationshipContext_ReturnsInvalidInput()
    {
        var charId = await SeedCharacterStateAsync();
        var targetId = Guid.NewGuid();
        await SeedRelationshipAsync(charId, RelationshipTargetType.User, targetId, RelationshipType.Stranger, trust: 0, affection: 0, familiarity: 0);

        var conflictingContext = new CharacterRelationshipContext(
            TargetId: targetId,
            TargetType: RelationshipTargetType.User,
            RelationshipType: RelationshipType.Romantic, // Conflict!
            Trust: 100,
            Affection: 100,
            Familiarity: 100
        );

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Hello", UserId: targetId);
        var context = CreateContext(charId, cognitiveEvent: userEvent, relationshipContext: conflictingContext);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, result.Status);
        Assert.Contains("Conflicting RelationshipContext detected", result.Message);
    }

    [Fact]
    public async Task RunAsync_WhenCallerInjectsRelationshipContext_ViaPerceptionContext_ReturnsInvalidInput()
    {
        var charId = await SeedCharacterStateAsync();
        var targetId = Guid.NewGuid();

        var injectedContext = new CharacterRelationshipContext(
            TargetId: targetId,
            TargetType: RelationshipTargetType.User,
            RelationshipType: RelationshipType.Friend,
            Trust: 50,
            Affection: 50,
            Familiarity: 50
        );

        var perceptionCtx = new CharacterPerceptionContext(
            EvaluatedAtUtc: FixedOccurredAt.UtcDateTime,
            CharacterId: charId,
            RelationshipContext: injectedContext
        );

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Hello", UserId: targetId);
        var context = CreateContext(charId, cognitiveEvent: userEvent, perceptionContext: perceptionCtx);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, result.Status);
        Assert.Contains("PerceptionContext.RelationshipContext cannot be pre-populated by caller", result.Message);
    }

    [Fact]
    public async Task RunAsync_WhenEventHasNoTarget_RelationshipContextIsNull()
    {
        var charId = await SeedCharacterStateAsync();
        var worldEvent = new WorldCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Thunderstorm");

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId, cognitiveEvent: worldEvent);

        var result = await service.RunAsync(context);

        Assert.Null(result.RelationshipContext);
        Assert.Null(result.RelationshipFeedback);
    }

    #endregion

    #region 3. Feedback Tests

    [Fact]
    public async Task RunAsync_WithUserMessage_ProducesRelationshipFeedback()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var targetId = Guid.NewGuid();
        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Good morning!", UserId: targetId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId, cognitiveEvent: userEvent);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.NotNull(result.RelationshipFeedback);
        Assert.Equal(charId, result.RelationshipFeedback.CharacterId);
        Assert.Equal(targetId, result.RelationshipFeedback.TargetId);
        Assert.Equal(RelationshipTargetType.User, result.RelationshipFeedback.TargetType);
        Assert.Equal(1, result.RelationshipFeedback.TrustDelta);
        Assert.Equal(1, result.RelationshipFeedback.AffectionDelta);
        Assert.Equal(1, result.RelationshipFeedback.FamiliarityDelta);

        // Verify DB aggregate updated
        await using var verifyDb = new CoreDbContext(_options);
        var rel = await verifyDb.CharacterRelationships.SingleAsync(r => r.CharacterId == charId && r.TargetId == targetId);
        Assert.Equal(1, rel.Trust);
        Assert.Equal(1, rel.Affection);
        Assert.Equal(1, rel.Familiarity);

        // Verify transition ledger row persisted
        var transition = await verifyDb.CharacterRelationshipTransitions.SingleAsync(t => t.CharacterId == charId && t.ExecutionId == context.ExecutionId);
        Assert.NotNull(transition);
        Assert.Equal(1, transition.TrustDelta);
        Assert.NotEmpty(transition.TransitionFingerprint);
    }

    [Fact]
    public async Task Feedback_WhenExecutionFails_AppliesFailureFeedback()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var targetId = Guid.NewGuid();
        await SeedRelationshipAsync(charId, RelationshipTargetType.User, targetId, trust: 20, affection: 20, familiarity: 10);

        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Do something impossible!", UserId: targetId);
        var failingService = new FailingActionExecutionService();

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db, actionExecutionService: failingService);
        var context = CreateContext(charId, cognitiveEvent: userEvent);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.ConcurrencyConflict, result.Status);
        Assert.NotNull(result.RelationshipFeedback);
        Assert.Equal(-1, result.RelationshipFeedback.TrustDelta);
        Assert.Equal(0, result.RelationshipFeedback.AffectionDelta);
        Assert.Equal(1, result.RelationshipFeedback.FamiliarityDelta);

        await using var verifyDb = new CoreDbContext(_options);
        var rel = await verifyDb.CharacterRelationships.SingleAsync(r => r.CharacterId == charId && r.TargetId == targetId);
        Assert.Equal(19, rel.Trust); // 20 - 1 = 19
        Assert.Equal(20, rel.Affection); // 20 + 0 = 20
        Assert.Equal(11, rel.Familiarity); // 10 + 1 = 11
    }

    #endregion

    #region 4. Idempotency & Replay Tests

    [Fact]
    public async Task RunAsync_SameExecutionId_ReplayReturnsPersistedRelationshipFeedback_WithoutDoubleApplyingDeltas()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var targetId = Guid.NewGuid();
        var sharedExecutionId = Guid.NewGuid();
        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Hello again", UserId: targetId);

        // Run 1: Initial execution
        await using (var db1 = new CoreDbContext(_options))
        {
            var service1 = CreateService(db1);
            var ctx1 = CreateContext(charId, executionId: sharedExecutionId, cognitiveEvent: userEvent);
            var res1 = await service1.RunAsync(ctx1);
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, res1.Status);
            Assert.NotNull(res1.RelationshipFeedback);
        }

        // Run 2: Replay with AlreadyExecuted
        await using (var db2 = new CoreDbContext(_options))
        {
            var alreadyExecService = new AlreadyExecutedActionExecutionService();
            var service2 = CreateService(db2, actionExecutionService: alreadyExecService);
            var ctx2 = CreateContext(charId, executionId: sharedExecutionId, cognitiveEvent: userEvent);
            var res2 = await service2.RunAsync(ctx2);

            Assert.Equal(CharacterCognitiveCycleStatus.AlreadyExecuted, res2.Status);
            Assert.NotNull(res2.RelationshipFeedback);
            Assert.Equal(sharedExecutionId, res2.RelationshipFeedback.ExecutionId);
        }

        // Assert: Database only has 1 transition row and deltas were NOT applied twice
        await using var verifyDb = new CoreDbContext(_options);
        var transitions = await verifyDb.CharacterRelationshipTransitions
            .Where(t => t.CharacterId == charId && t.ExecutionId == sharedExecutionId)
            .ToListAsync();
        Assert.Single(transitions);

        var rel = await verifyDb.CharacterRelationships.SingleAsync(r => r.CharacterId == charId && r.TargetId == targetId);
        Assert.Equal(1, rel.Trust); // Not 2!
        Assert.Equal(1, rel.Affection); // Not 2!
        Assert.Equal(1, rel.Familiarity); // Not 2!
    }

    [Fact]
    public async Task RunAsync_SameExecutionId_DifferentSemanticPayload_ReturnsIdempotencyConflict()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var targetId = Guid.NewGuid();
        var sharedExecutionId = Guid.NewGuid();

        // Seed DB directly with an existing transition for (charId, sharedExecutionId) with TrustDelta = 50
        await using (var seedDb = new CoreDbContext(_options))
        {
            var transition = new CharacterRelationshipTransition(
                characterId: charId,
                executionId: sharedExecutionId,
                targetId: targetId,
                targetType: RelationshipTargetType.User,
                trustDelta: 50, // Different delta!
                affectionDelta: 50,
                familiarityDelta: 50,
                oldRelationshipType: RelationshipType.Stranger,
                newRelationshipType: RelationshipType.Stranger,
                versionBefore: 1,
                versionAfter: 2,
                reason: "Prior custom transition",
                appliedAtUtc: DateTime.UtcNow
            );
            seedDb.CharacterRelationshipTransitions.Add(transition);
            await seedDb.SaveChangesAsync();
        }

        // Run cycle which would normally produce TrustDelta = 1
        await using (var db = new CoreDbContext(_options))
        {
            var service = CreateService(db);
            var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Hi", UserId: targetId);
            var context = CreateContext(charId, executionId: sharedExecutionId, cognitiveEvent: userEvent);

            var result = await service.RunAsync(context);

            Assert.Equal(CharacterCognitiveCycleStatus.IdempotencyConflict, result.Status);
            Assert.Contains("different semantic relationship feedback payload", result.Message);
        }
    }

    [Fact]
    public async Task RunAsync_DifferentExecutionIds_ApplyIndependently()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var targetId = Guid.NewGuid();
        var execId1 = Guid.NewGuid();
        var execId2 = Guid.NewGuid();
        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Consecutive msg", UserId: targetId);

        await using (var db1 = new CoreDbContext(_options))
        {
            var service1 = CreateService(db1);
            var res1 = await service1.RunAsync(CreateContext(charId, executionId: execId1, cognitiveEvent: userEvent));
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, res1.Status);
        }

        await using (var db2 = new CoreDbContext(_options))
        {
            var service2 = CreateService(db2);
            var res2 = await service2.RunAsync(CreateContext(charId, executionId: execId2, cognitiveEvent: userEvent));
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, res2.Status);
        }

        await using var verifyDb = new CoreDbContext(_options);
        var count = await verifyDb.CharacterRelationshipTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(2, count);

        var rel = await verifyDb.CharacterRelationships.SingleAsync(r => r.CharacterId == charId && r.TargetId == targetId);
        Assert.Equal(2, rel.Trust); // 1 + 1 = 2
        Assert.Equal(2, rel.Affection); // 1 + 1 = 2
        Assert.Equal(2, rel.Familiarity); // 1 + 1 = 2
    }

    #endregion

    #region 5. Concurrency Tests

    [Fact]
    public async Task ConcurrentRelationshipTransitions_DoNotSilentlyOverwrite()
    {
        var charId = await SeedCharacterStateAsync();
        var targetId = Guid.NewGuid();
        await SeedRelationshipAsync(charId, RelationshipTargetType.User, targetId, trust: 10, affection: 10, familiarity: 10);

        // Run 5 concurrent transitions on the same relationship using separate DbContext instances
        var tasks = Enumerable.Range(1, 5).Select(async i =>
        {
            await using var db = new CoreDbContext(_options);
            var transitionService = new CharacterRelationshipTransitionService(db, NullLogger<CharacterRelationshipTransitionService>.Instance);
            return await transitionService.ApplyTransitionAsync(
                characterId: charId,
                executionId: Guid.NewGuid(),
                targetId: targetId,
                targetType: RelationshipTargetType.User,
                trustDelta: 2,
                affectionDelta: 3,
                familiarityDelta: 1,
                newRelationshipType: null,
                reason: $"Concurrent task {i}",
                occurredAtUtc: DateTimeOffset.UtcNow
            );
        });

        var results = await Task.WhenAll(tasks);

        // Assert: All 5 transitions succeeded
        Assert.All(results, Assert.NotNull);

        // Verify final aggregate state is exactly 10 + 5*delta (no lost updates!)
        await using var verifyDb = new CoreDbContext(_options);
        var rel = await verifyDb.CharacterRelationships.SingleAsync(r => r.CharacterId == charId && r.TargetId == targetId);
        Assert.Equal(10 + (5 * 2), rel.Trust); // 20
        Assert.Equal(10 + (5 * 3), rel.Affection); // 25
        Assert.Equal(10 + (5 * 1), rel.Familiarity); // 15
    }

    #endregion

    #region 6. Cognitive Pipeline Integration Tests

    [Fact]
    public async Task RunAsync_RelationshipFeedback_DoesNotMutate_CharacterState()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m, energy: 75m, stress: 30m, version: 1);
        var targetId = Guid.NewGuid();
        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Compliment", UserId: targetId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId, cognitiveEvent: userEvent);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.NotNull(result.RelationshipFeedback);

        // Hard Invariant Check: Verify CharacterState was mutated ONLY by ActionExecution (Eat), NOT by RelationshipFeedback!
        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);

        // Eat action reduces hunger by -30 (from 90 to 60) and bumps version to 2
        Assert.Equal(60m, state.Hunger);
        Assert.Equal(2, state.Version);

        // Relationship dimensions are NOT in CharacterState
        Assert.DoesNotContain(state.GetType().GetProperties(), p => p.Name == "Trust" || p.Name == "Affection" || p.Name == "Familiarity");
    }

    [Fact]
    public async Task RunAsync_RelationshipAndMemory_RemainStrictlySeparated()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var targetId = Guid.NewGuid();
        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Hi", UserId: targetId);

        // Inject failing memory retrieval service
        var failingMemoryRetrieval = new FailingMemoryRetrievalService();

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db, memoryRetrievalService: failingMemoryRetrieval);
        var context = CreateContext(charId, cognitiveEvent: userEvent);

        var result = await service.RunAsync(context);

        // Memory retrieval failed and fell back gracefully to empty, while RelationshipContext succeeded authoritatively!
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.Equal(CharacterMemoryContext.Empty, result.MemoryContext);
        Assert.NotNull(result.RelationshipContext);
        Assert.Equal(targetId, result.RelationshipContext.TargetId);
        Assert.NotNull(result.RelationshipFeedback);
    }

    [Fact]
    public async Task RunAsync_WhenRelationshipPersistenceFails_CommittedStateTransitionIsNotRolledBack()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m, version: 1);
        var targetId = Guid.NewGuid();
        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Hi", UserId: targetId);

        // Inject failing relationship feedback service
        var failingRelFeedback = new FailingRelationshipFeedbackService();

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db, relationshipFeedbackService: failingRelFeedback);
        var context = CreateContext(charId, cognitiveEvent: userEvent);

        var result = await service.RunAsync(context);

        // Invariant: Non-fatal failure does not roll back action or cycle result!
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.Null(result.RelationshipFeedback);

        // Verify state transition remains safely committed in DB
        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(2, state.Version); // State was committed!

        var transition = await verifyDb.CharacterStateTransitions.SingleAsync(t => t.CharacterId == charId);
        Assert.NotNull(transition);
    }

    #endregion

    #region 7. Determinism Test

    [Fact]
    public async Task RunAsync_SameInputs_ProduceDeterministicOutputs()
    {
        var charId1 = await SeedCharacterStateAsync(hunger: 90m);
        var charId2 = await SeedCharacterStateAsync(hunger: 90m);
        var targetId = Guid.NewGuid();

        await SeedRelationshipAsync(charId1, RelationshipTargetType.User, targetId, RelationshipType.Acquaintance, trust: 25, affection: 25, familiarity: 25);
        await SeedRelationshipAsync(charId2, RelationshipTargetType.User, targetId, RelationshipType.Acquaintance, trust: 25, affection: 25, familiarity: 25);

        var sharedEventId = Guid.NewGuid();
        var sharedExecutionId1 = Guid.NewGuid();
        var sharedExecutionId2 = Guid.NewGuid();

        var evt1 = new UserMessageCognitiveEvent(sharedEventId, charId1, FixedOccurredAt, "Deterministic test", UserId: targetId);
        var evt2 = new UserMessageCognitiveEvent(sharedEventId, charId2, FixedOccurredAt, "Deterministic test", UserId: targetId);

        CharacterCognitiveCycleResult res1;
        CharacterCognitiveCycleResult res2;

        await using (var db1 = new CoreDbContext(_options))
        {
            var s1 = CreateService(db1);
            res1 = await s1.RunAsync(CreateContext(charId1, executionId: sharedExecutionId1, cognitiveEvent: evt1));
        }

        await using (var db2 = new CoreDbContext(_options))
        {
            var s2 = CreateService(db2);
            res2 = await s2.RunAsync(CreateContext(charId2, executionId: sharedExecutionId2, cognitiveEvent: evt2));
        }

        Assert.Equal(res1.Status, res2.Status);
        Assert.Equal(res1.ActionProposal?.Proposal?.Type, res2.ActionProposal?.Proposal?.Type);
        Assert.Equal(res1.RelationshipFeedback?.TrustDelta, res2.RelationshipFeedback?.TrustDelta);
        Assert.Equal(res1.RelationshipFeedback?.AffectionDelta, res2.RelationshipFeedback?.AffectionDelta);
        Assert.Equal(res1.RelationshipFeedback?.FamiliarityDelta, res2.RelationshipFeedback?.FamiliarityDelta);
    }

    #endregion

    #region Test Doubles

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

    private sealed class FailingMemoryRetrievalService : ICharacterMemoryRetrievalService
    {
        public Task<CharacterMemoryContext> RetrieveRelevantAsync(
            Guid characterId,
            CharacterPerceptionContext perceptionContext,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated database failure during memory retrieval.");
        }
    }

    private sealed class FailingRelationshipFeedbackService : ICharacterRelationshipFeedbackService
    {
        public Task<CharacterRelationshipFeedback?> RecordFeedbackAsync(
            CharacterCognitiveCycleContext cycleContext,
            CharacterCognitiveCycleResult cycleResult,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated database connection loss during relationship feedback.");
        }
    }

    #endregion
}
