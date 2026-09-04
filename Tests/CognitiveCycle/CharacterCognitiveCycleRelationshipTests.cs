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
using Microsoft.EntityFrameworkCore.Storage;
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
    public async Task Feedback_WhenExecutionFails_WithInfrastructureError_DoesNotMutateRelationship()
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

        // Infrastructure concurrency failure must NOT result in negative social feedback
        Assert.Equal(CharacterCognitiveCycleStatus.ConcurrencyConflict, result.Status);
        Assert.Null(result.RelationshipFeedback);

        await using var verifyDb = new CoreDbContext(_options);
        var rel = await verifyDb.CharacterRelationships.SingleAsync(r => r.CharacterId == charId && r.TargetId == targetId);
        Assert.Equal(20, rel.Trust); // Untouched
        Assert.Equal(20, rel.Affection); // Untouched
        Assert.Equal(10, rel.Familiarity); // Untouched

        // No transition recorded
        var transitionCount = await verifyDb.CharacterRelationshipTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(0, transitionCount);
    }

    [Fact]
    public async Task Feedback_WhenCompletedWithoutAction_AppliesFamiliarityOnlyFeedback()
    {
        // All needs satisfied: Hunger 0, Energy 100, Stress 0, SocialNeed 0, Comfort 100
        var charId = await SeedCharacterStateAsync(hunger: 0m, energy: 100m, stress: 0m, socialNeed: 0m, comfort: 100m);
        var targetId = Guid.NewGuid();
        await SeedRelationshipAsync(charId, RelationshipTargetType.User, targetId, trust: 20, affection: 20, familiarity: 10);

        var userEvent = new UserMessageCognitiveEvent(Guid.NewGuid(), charId, FixedOccurredAt, "Just passing by", UserId: targetId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);
        var context = CreateContext(charId, cognitiveEvent: userEvent);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithoutAction, result.Status);
        Assert.NotNull(result.RelationshipFeedback);
        Assert.Equal(0, result.RelationshipFeedback.TrustDelta);
        Assert.Equal(0, result.RelationshipFeedback.AffectionDelta);
        Assert.Equal(1, result.RelationshipFeedback.FamiliarityDelta);

        await using var verifyDb = new CoreDbContext(_options);
        var rel = await verifyDb.CharacterRelationships.SingleAsync(r => r.CharacterId == charId && r.TargetId == targetId);
        Assert.Equal(20, rel.Trust); // 20 + 0 = 20
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
    public async Task CharacterRelationship_VersionConcurrencyToken_WorkerAWins_WorkerBThrowsDbUpdateConcurrencyException()
    {
        var charId = await SeedCharacterStateAsync();
        var targetId = Guid.NewGuid();
        Guid relId;

        // Seed relationship at Version 1
        await using (var seedDb = new CoreDbContext(_options))
        {
            var rel = CharacterRelationship.Create(charId, RelationshipTargetType.User, targetId, trust: 10, affection: 10, familiarity: 10);
            await seedDb.CharacterRelationships.AddAsync(rel);
            await seedDb.SaveChangesAsync();
            relId = rel.Id;
        }

        // Context A and Context B load the exact same relationship at Version 1 concurrently
        await using var dbA = new CoreDbContext(_options);
        await using var dbB = new CoreDbContext(_options);

        var relA = await dbA.CharacterRelationships.SingleAsync(r => r.Id == relId);
        var relB = await dbB.CharacterRelationships.SingleAsync(r => r.Id == relId);

        Assert.Equal(1u, relA.Version);
        Assert.Equal(1u, relB.Version);

        // Worker A updates Trust (+5) and commits -> Version advances to 2
        relA.ApplyTrustDelta(5);
        await dbA.SaveChangesAsync();

        // Worker B attempts to update from stale Version 1 (Trust +10)
        relB.ApplyTrustDelta(10);

        // EF Core optimistic concurrency token (Version) MUST detect stale update and throw DbUpdateConcurrencyException
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
        Assert.NotNull(ex);

        // Verify database state in an independent context: Version remains at 2 with Worker A's authoritative update (no silent lost updates!)
        await using var verifyDb = new CoreDbContext(_options);
        var authoritativeRel = await verifyDb.CharacterRelationships.SingleAsync(r => r.Id == relId);
        Assert.Equal(2u, authoritativeRel.Version);
        Assert.Equal(15, authoritativeRel.Trust); // 10 + 5 = 15 (Worker B's stale 10 was rejected)
    }

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

        // Assert: All 5 transitions succeeded via concurrency retry
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

    #region 8. Migration & Backfill Regression Tests

    [Fact]
    public async Task Migration_LegacyRelationshipsWithMultipleUsers_BackfillsTargetIdAndPreservesUniqueness()
    {
        var charId = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var user3 = Guid.NewGuid();

        // 1. Simulate pre-PR48 schema: Drop new unique index temporarily
        await using (var preDb = new CoreDbContext(_options))
        {
            await preDb.Database.ExecuteSqlRawAsync(@"DROP INDEX IF EXISTS ""IX_CharacterRelationships_CharacterId_TargetType_TargetId"";");

            var character = new Character("Hero", "Protagonist", "https://avatar.png", "Brave", "Hi", "Fantasy") { Id = charId };
            await preDb.Characters.AddAsync(character);

            // Legacy relationship factory uses (characterId, userId, initialAffection, ...)
            var rel1 = CharacterRelationship.Create(charId, user1, initialAffection: 50, CharacterMood.Neutral);
            var rel2 = CharacterRelationship.Create(charId, user2, initialAffection: -30, CharacterMood.Neutral);
            var rel3 = CharacterRelationship.Create(charId, user3, initialAffection: 120, CharacterMood.Neutral);

            await preDb.CharacterRelationships.AddRangeAsync(rel1, rel2, rel3);
            await preDb.SaveChangesAsync();

            // Simulate raw unmigrated state where TargetId was defaulted to Guid.Empty across all existing rows
            await preDb.Database.ExecuteSqlRawAsync(
                @"UPDATE ""CharacterRelationships"" SET ""TargetId"" = '00000000-0000-0000-0000-000000000000', ""TargetType"" = 'User', ""Affection"" = 0;");
        }

        // 2. Execute authoritative PR48 migration step: Backfill THEN Create Unique Index
        await using (var migrateDb = new CoreDbContext(_options))
        {
            // Step 2a: Backfill existing rows
            await migrateDb.Database.ExecuteSqlRawAsync(@"
                UPDATE ""CharacterRelationships""
                SET ""TargetType"" = 'User',
                    ""TargetId"" = ""UserId"",
                    ""Affection"" = CASE 
                        WHEN ""AffectionScore"" < 0 THEN 0 
                        WHEN ""AffectionScore"" > 100 THEN 100 
                        ELSE ""AffectionScore"" 
                    END;
            ");

            // Step 2b: Create unique composite index (CharacterId, TargetType, TargetId)
            // (If backfill hadn't run, this would fail with duplicate key on Guid.Empty)
            await migrateDb.Database.ExecuteSqlRawAsync(@"
                CREATE UNIQUE INDEX ""IX_CharacterRelationships_CharacterId_TargetType_TargetId"" 
                ON ""CharacterRelationships"" (""CharacterId"", ""TargetType"", ""TargetId"");
            ");
        }

        // 3. Verify in independent context:
        await using var verifyDb = new CoreDbContext(_options);
        var allRels = await verifyDb.CharacterRelationships
            .Where(r => r.CharacterId == charId)
            .OrderBy(r => r.UserId)
            .ToListAsync();

        Assert.Equal(3, allRels.Count);

        var migratedRel1 = allRels.Single(r => r.UserId == user1);
        Assert.Equal(RelationshipTargetType.User, migratedRel1.TargetType);
        Assert.Equal(user1, migratedRel1.TargetId);
        Assert.Equal(50, migratedRel1.Affection);

        var migratedRel2 = allRels.Single(r => r.UserId == user2);
        Assert.Equal(RelationshipTargetType.User, migratedRel2.TargetType);
        Assert.Equal(user2, migratedRel2.TargetId);
        Assert.Equal(0, migratedRel2.Affection); // Clamped from -30 to 0

        var migratedRel3 = allRels.Single(r => r.UserId == user3);
        Assert.Equal(RelationshipTargetType.User, migratedRel3.TargetType);
        Assert.Equal(user3, migratedRel3.TargetId);
        Assert.Equal(100, migratedRel3.Affection); // Clamped from 120 to 100

        // 4. Verify authoritative target-based retrieval finds the migrated legacy record
        var repo = new Infrastructure.Persistence.Repositories.CharacterRelationshipRepository(verifyDb);
        var retrieved = await repo.GetByTargetAsync(charId, RelationshipTargetType.User, user1);
        Assert.NotNull(retrieved);
        Assert.Equal(migratedRel1.Id, retrieved.Id);

        // 5. Verify GetOrCreateByTargetAsync does NOT create duplicate relationships
        var retrievedOrCreated = await repo.GetOrCreateByTargetAsync(charId, RelationshipTargetType.User, user1);
        Assert.Equal(migratedRel1.Id, retrievedOrCreated.Id);

        var totalCount = await verifyDb.CharacterRelationships.CountAsync(r => r.CharacterId == charId);
        Assert.Equal(3, totalCount);
    }

    [Fact]
    public async Task GetOrCreateByTargetAsync_WhenConcurrentInsertConflictOccurs_DetachesLocalEntityAndSubsequentSaveChangesSucceeds()
    {
        var charId = await SeedCharacterStateAsync();
        var targetId = Guid.NewGuid();

        // 1. Configure an interceptor on dbLoser that simulates the exact race:
        // After dbLoser's initial lookup (which returns null because no row exists yet),
        // and right before dbLoser executes its INSERT inside SaveChangesAsync,
        // another worker concurrently inserts and commits the exact same (charId, targetType, targetId).
        var interceptor = new ConcurrentRelationshipInsertInterceptor(charId, targetId);
        var loserOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var dbLoser = new CoreDbContext(loserOptions);

        // Loser context tracks an unrelated entity to prove ChangeTracker is NOT indiscriminately cleared
        var executionId = Guid.NewGuid();
        var transition = new CharacterRelationshipTransition(
            characterId: charId,
            executionId: executionId,
            targetId: targetId,
            targetType: RelationshipTargetType.User,
            trustDelta: 5,
            affectionDelta: 5,
            familiarityDelta: 5,
            oldRelationshipType: RelationshipType.Stranger,
            newRelationshipType: RelationshipType.Stranger,
            versionBefore: 1,
            versionAfter: 2,
            reason: "Unrelated caller work",
            appliedAtUtc: DateTime.UtcNow
        );
        await dbLoser.CharacterRelationshipTransitions.AddAsync(transition);

        // 2. Loser calls GetOrCreateByTargetAsync for (charId, User, targetId).
        // - Initial lookup finds NULL (because winner has NOT inserted yet).
        // - Local newRelationship is created and added to ChangeTracker (EntityState.Added).
        // - dbLoser calls SaveChangesAsync.
        // - Interceptor fires in SavingChangesAsync and inserts the winner's relationship row into the DB.
        // - dbLoser attempts to insert newRelationship -> SQLite throws UNIQUE constraint violation -> EF throws DbUpdateException.
        // - catch (DbUpdateException) catches the exception, detaches ONLY newRelationship, and reloads the authoritative winner.
        var loserRepo = new Infrastructure.Persistence.Repositories.CharacterRelationshipRepository(dbLoser);
        var resolvedRel = await loserRepo.GetOrCreateByTargetAsync(charId, RelationshipTargetType.User, targetId);

        // Assert 1: The interceptor actually injected the concurrent row right during SaveChangesAsync
        Assert.True(interceptor.WasInjected);

        // Assert 2: Authoritative winner relationship is returned
        Assert.NotNull(resolvedRel);
        Assert.Equal(targetId, resolvedRel.TargetId);
        Assert.Equal(interceptor.InjectedRelationshipId, resolvedRel.Id);

        // Assert 3: The locally-created loser relationship is DETACHED (no CharacterRelationship left in Added state)
        var addedRelationships = dbLoser.ChangeTracker.Entries<CharacterRelationship>()
            .Where(e => e.State == EntityState.Added)
            .ToList();
        Assert.Empty(addedRelationships);

        // Assert 4: The unrelated entity is STILL tracked as Added (proves ChangeTracker.Clear() was NOT called)
        var transitionEntry = dbLoser.Entry(transition);
        Assert.Equal(EntityState.Added, transitionEntry.State);

        // Assert 5: A subsequent SaveChangesAsync on the same dbLoser succeeds (proves it doesn't re-insert the duplicate)
        await dbLoser.SaveChangesAsync();

        // Assert 6 & 7: Verify in independent context: Exactly 1 relationship exists, and the unrelated entity was committed
        await using var verifyDb = new CoreDbContext(_options);
        var relCount = await verifyDb.CharacterRelationships.CountAsync(r => r.CharacterId == charId && r.TargetId == targetId);
        Assert.Equal(1, relCount);

        var savedTransition = await verifyDb.CharacterRelationshipTransitions.SingleOrDefaultAsync(t => t.ExecutionId == executionId);
        Assert.NotNull(savedTransition);
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

    private sealed class ConcurrentRelationshipInsertInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private readonly Guid _charId;
        private readonly Guid _targetId;
        public bool WasInjected { get; private set; }
        public Guid InjectedRelationshipId { get; private set; }

        public ConcurrentRelationshipInsertInterceptor(Guid charId, Guid targetId)
        {
            _charId = charId;
            _targetId = targetId;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!WasInjected && eventData.Context != null)
            {
                WasInjected = true;
                InjectedRelationshipId = Guid.NewGuid();

                using var cmd = eventData.Context.Database.GetDbConnection().CreateCommand();
                if (eventData.Context.Database.CurrentTransaction != null)
                {
                    cmd.Transaction = eventData.Context.Database.CurrentTransaction.GetDbTransaction();
                }

                cmd.CommandText = @"
                    INSERT INTO ""CharacterRelationships"" (
                        ""Id"", ""CharacterId"", ""TargetType"", ""TargetId"", ""RelationshipType"",
                        ""Trust"", ""Affection"", ""Familiarity"", ""UserId"", ""AffectionScore"",
                        ""CurrentMood"", ""MoodIntensity"", ""LastInteractedAt"", ""Version"", ""EventsJson"", ""CreatedAt"", ""IsSoftDeleted""
                    ) VALUES (
                        @id, @charId, @targetType, @targetId, @relType,
                        0, 0, 0, @userId, 0,
                        'Neutral', 20, @now, 1, '[]', @now, 0
                    );";

                var pId = cmd.CreateParameter();
                pId.ParameterName = "@id";
                pId.Value = InjectedRelationshipId;
                cmd.Parameters.Add(pId);

                var pCharId = cmd.CreateParameter();
                pCharId.ParameterName = "@charId";
                pCharId.Value = _charId;
                cmd.Parameters.Add(pCharId);

                var pTargetType = cmd.CreateParameter();
                pTargetType.ParameterName = "@targetType";
                pTargetType.Value = nameof(RelationshipTargetType.User);
                cmd.Parameters.Add(pTargetType);

                var pTargetId = cmd.CreateParameter();
                pTargetId.ParameterName = "@targetId";
                pTargetId.Value = _targetId;
                cmd.Parameters.Add(pTargetId);

                var pRelType = cmd.CreateParameter();
                pRelType.ParameterName = "@relType";
                pRelType.Value = nameof(RelationshipType.Stranger);
                cmd.Parameters.Add(pRelType);

                var pUserId = cmd.CreateParameter();
                pUserId.ParameterName = "@userId";
                pUserId.Value = _targetId;
                cmd.Parameters.Add(pUserId);

                var pNow = cmd.CreateParameter();
                pNow.ParameterName = "@now";
                pNow.Value = DateTime.UtcNow.ToString("O");
                cmd.Parameters.Add(pNow);

                cmd.ExecuteNonQuery();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    #endregion
}
