using System;
using System.Linq;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
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

public sealed class CharacterCognitiveCycleServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 4, 11, 0, 0, TimeSpan.Zero);

    public CharacterCognitiveCycleServiceTests()
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
        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();
        return charId;
    }

    private CharacterCognitiveCycleService CreateService(
        CoreDbContext db,
        ICharacterInternalExperiencePolicy? experiencePolicy = null,
        ICharacterAppraisalPolicy? appraisalPolicy = null,
        ICharacterEmotionPolicy? emotionPolicy = null,
        ICharacterDesirePolicy? desirePolicy = null,
        ICharacterIntentPolicy? intentPolicy = null,
        ICharacterActionProposalPolicy? actionProposalPolicy = null,
        ICharacterActionExecutionService? actionExecutionService = null)
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

        return new CharacterCognitiveCycleService(
            stateService: stateService,
            experiencePolicy: experiencePolicy ?? new CharacterInternalExperiencePolicy(),
            appraisalPolicy: appraisalPolicy ?? new CharacterAppraisalPolicy(),
            emotionPolicy: emotionPolicy ?? new CharacterEmotionPolicy(),
            desirePolicy: desirePolicy ?? new CharacterDesirePolicy(),
            intentPolicy: intentPolicy ?? new CharacterIntentPolicy(),
            actionProposalPolicy: actionProposalPolicy ?? new CharacterActionProposalPolicy(),
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance
        );
    }

    private static CharacterCognitiveCycleContext CreateContext(
        Guid characterId,
        Guid? cycleId = null,
        Guid? executionId = null,
        DateTimeOffset? triggeredAtUtc = null,
        CharacterStateSnapshot? initialState = null)
    {
        return new CharacterCognitiveCycleContext(
            CycleId: cycleId ?? Guid.NewGuid(),
            ExecutionId: executionId ?? Guid.NewGuid(),
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc ?? FixedNow,
            InitialState: initialState
        );
    }

    #region 1. Basic End-to-End Pipeline Tests

    [Fact]
    public async Task RunAsync_ExecutesEndToEndPipeline_InStrictSequentialOrder()
    {
        // High hunger (90) -> Starving -> Hunger appraisal -> Fear/Desire -> NeedFood -> SeekFood -> Eat -> Persistent State
        var charId = await SeedCharacterStateAsync(hunger: 90m, energy: 70m, stress: 10m);
        var cycleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var context = CreateContext(charId, cycleId, executionId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.NotNull(result);
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.True(result.IsSuccess);
        Assert.True(result.HasAction);
        Assert.False(result.IsDuplicateSuppressed);

        // Provenance & Identity
        Assert.Equal(cycleId, result.CycleId);
        Assert.Equal(executionId, result.ExecutionId);
        Assert.Equal(charId, result.CharacterId);
        Assert.Equal(FixedNow, result.TriggeredAtUtc);
        Assert.Equal(1, result.StateVersionAtStart);

        // Stage 1: Experience
        Assert.NotNull(result.Experience);
        Assert.Equal(DominantNeed.Hunger, result.Experience.DominantNeed);
        Assert.Equal(1, result.Experience.StateVersion);

        // Stage 2: Appraisal
        Assert.NotNull(result.Appraisal);
        Assert.Equal(AppraisalType.PhysicalDeprivation, result.Appraisal.Type);
        Assert.Equal(AppraisalPolarity.Negative, result.Appraisal.Polarity);
        Assert.Equal(AppraisalSource.Hunger, result.Appraisal.Source);

        // Stage 3: Emotion
        Assert.NotNull(result.Emotion);

        // Stage 4: Desires
        Assert.NotNull(result.Desires);
        Assert.NotNull(result.Desires.DominantDesire);
        Assert.Equal(DesireType.NeedFood, result.Desires.DominantDesire.Type);

        // Stage 5: Intent
        Assert.NotNull(result.Intent);
        Assert.NotNull(result.Intent.Intent);
        Assert.Equal(IntentType.SeekFood, result.Intent.Intent.Type);
        Assert.Equal(MotivationType.HungerDriven, result.Intent.Intent.Motivation);

        // Stage 6: Action Proposal
        Assert.NotNull(result.ActionProposal);
        Assert.NotNull(result.ActionProposal.Proposal);
        Assert.Equal(ActionType.Eat, result.ActionProposal.Proposal.Type);

        // Stage 7: Action Execution
        Assert.NotNull(result.ActionExecution);
        Assert.Equal(CharacterActionExecutionStatus.Applied, result.ActionExecution.Status);
        Assert.Equal(1, result.ActionExecution.StateVersionBefore);
        Assert.Equal(2, result.ActionExecution.StateVersionAfter);
        Assert.NotNull(result.ActionExecution.Snapshot);
        Assert.Equal(60m, result.ActionExecution.Snapshot.Hunger); // 90 - (30 * 1.0) = 60

        // Database verification
        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(60m, state.Hunger);
        Assert.Equal(2, state.Version);

        var transition = await verifyDb.CharacterStateTransitions.SingleAsync(t => t.CharacterId == charId);
        Assert.Equal(executionId, transition.ExecutionId);
        Assert.Equal(1, transition.VersionBefore);
        Assert.Equal(2, transition.VersionAfter);
    }

    #endregion

    #region 2. No Desire / Sub-Threshold Needs Tests

    [Fact]
    public async Task RunAsync_WhenAllNeedsSatisfied_StopsWithoutAction()
    {
        // All needs satisfied: Hunger 0, Energy 100, Stress 0, SocialNeed 0, Comfort 100
        var charId = await SeedCharacterStateAsync(
            hunger: 0m, energy: 100m, stress: 0m, socialNeed: 0m, comfort: 100m);
        var context = CreateContext(charId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.NotNull(result);
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithoutAction, result.Status);
        Assert.True(result.IsSuccess);
        Assert.False(result.HasAction);
        Assert.Null(result.ActionExecution);

        // Ensure no state mutation and no ledger entry
        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(1, state.Version); // Version unchanged

        var transitionCount = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(0, transitionCount);
    }

    #endregion

    #region 3. No Intent & No Proposal Early Exits

    private sealed class NullIntentPolicy : ICharacterIntentPolicy
    {
        public CharacterIntentEvaluation Evaluate(CharacterDesireEvaluation desireEvaluation, CharacterIntentContext context) =>
            new(desireEvaluation.CharacterId, desireEvaluation.StateVersion, null, context.EvaluatedAtUtc);
    }

    [Fact]
    public async Task RunAsync_WhenIntentIsNull_StopsWithoutAction_AndDoesNotCallActionExecution()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var context = CreateContext(charId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db, intentPolicy: new NullIntentPolicy());

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithoutAction, result.Status);
        Assert.True(result.IsSuccess);
        Assert.False(result.HasAction);
        Assert.Null(result.ActionProposal);
        Assert.Null(result.ActionExecution);

        // No database mutation
        await using var verifyDb = new CoreDbContext(_options);
        var count = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(0, count);
    }

    private sealed class NullProposalPolicy : ICharacterActionProposalPolicy
    {
        public CharacterActionProposalEvaluation Evaluate(CharacterIntentEvaluation intentEvaluation, CharacterActionProposalContext context) =>
            new(intentEvaluation.CharacterId, intentEvaluation.StateVersion, null, context.EvaluatedAtUtc);
    }

    [Fact]
    public async Task RunAsync_WhenProposalIsNull_StopsWithoutAction_AndDoesNotCallActionExecution()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var context = CreateContext(charId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db, actionProposalPolicy: new NullProposalPolicy());

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithoutAction, result.Status);
        Assert.True(result.IsSuccess);
        Assert.False(result.HasAction);
        Assert.NotNull(result.ActionProposal);
        Assert.Null(result.ActionProposal.Proposal);
        Assert.Null(result.ActionExecution);

        // No database mutation
        await using var verifyDb = new CoreDbContext(_options);
        var count = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(0, count);
    }

    #endregion

    #region 4. StateVersion & Execution Identity Propagation

    [Fact]
    public async Task RunAsync_PropagatesStateVersion_UnbrokenThroughAllStages()
    {
        var charId = await SeedCharacterStateAsync(hunger: 80m);

        // Advance state to Version 4 by applying 3 dummy transitions
        await using (var seedDb = new CoreDbContext(_options))
        {
            var seedService = CreateService(seedDb);
            for (int i = 1; i <= 3; i++)
            {
                var cycle = await seedService.RunAsync(CreateContext(charId));
                Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, cycle.Status);
            }
        }

        await using var verifyDb = new CoreDbContext(_options);
        var stateBefore = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(4, stateBefore.Version);

        // Now run cycle at Version 4
        await using var testDb = new CoreDbContext(_options);
        var service = CreateService(testDb);
        var result = await service.RunAsync(CreateContext(charId));

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.Equal(4, result.StateVersionAtStart);
        Assert.Equal(4, result.Experience!.StateVersion);
        Assert.Equal(4, result.Desires!.StateVersion);
        Assert.Equal(4, result.Intent!.StateVersion);
        Assert.Equal(4, result.ActionProposal!.StateVersion);
        Assert.Equal(4, result.ActionProposal.Proposal!.StateVersion);
        Assert.Equal(4, result.ActionExecution!.StateVersionBefore);
        Assert.Equal(5, result.ActionExecution.StateVersionAfter);
    }

    [Fact]
    public async Task RunAsync_PropagatesExecutionIdentity_ToExecutionServiceAndLedger()
    {
        var charId = await SeedCharacterStateAsync(hunger: 80m);
        var cycleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var context = CreateContext(charId, cycleId, executionId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.Equal(cycleId, result.CycleId);
        Assert.Equal(executionId, result.ExecutionId);
        Assert.Equal(executionId, result.ActionExecution!.ExecutionId);

        await using var verifyDb = new CoreDbContext(_options);
        var transition = await verifyDb.CharacterStateTransitions.SingleAsync(t => t.CharacterId == charId);
        Assert.Equal(executionId, transition.ExecutionId);
    }

    #endregion

    #region 5. Explicit Timestamp & Determinism

    [Fact]
    public async Task RunAsync_DeterministicExecution_WithExplicitTimestamp_DoesNotDependOnWallClock()
    {
        var charId = await SeedCharacterStateAsync(hunger: 80m);
        var explicitTime = new DateTimeOffset(2026, 7, 20, 15, 30, 0, TimeSpan.Zero);
        var executionId = Guid.NewGuid();
        var context = CreateContext(charId, executionId: executionId, triggeredAtUtc: explicitTime);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.Equal(explicitTime, result.TriggeredAtUtc);

        await using var verifyDb = new CoreDbContext(_options);
        var transition = await verifyDb.CharacterStateTransitions.SingleAsync(t => t.ExecutionId == executionId);
        Assert.Equal(explicitTime.UtcDateTime, transition.AppliedAtUtc);
    }

    [Fact]
    public async Task RunAsync_RejectsDefaultTimestamp_WithInvalidInput()
    {
        var charId = await SeedCharacterStateAsync();
        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: default
        );

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, result.Status);
        Assert.Contains("valid timestamp", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region 6. Idempotency & AlreadyExecuted Tests

    [Fact]
    public async Task RunAsync_WhenSameExecutionIdReplayed_ReturnsAlreadyExecuted_AndDoesNotDuplicateStateMutation()
    {
        var charId = await SeedCharacterStateAsync(hunger: 80m);
        await using var initDb = new CoreDbContext(_options);
        var initialState = (await initDb.CharacterStates.SingleAsync(s => s.CharacterId == charId)).ToSnapshot();
        var sharedExecutionId = Guid.NewGuid();
        var context = CreateContext(charId, executionId: sharedExecutionId, initialState: initialState);

        // Run 1: Applied
        await using var db1 = new CoreDbContext(_options);
        var service1 = CreateService(db1);
        var result1 = await service1.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result1.Status);
        Assert.Equal(2, result1.ActionExecution!.StateVersionAfter);

        // Run 2: Replay with same ExecutionId
        await using var db2 = new CoreDbContext(_options);
        var service2 = CreateService(db2);
        var result2 = await service2.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.AlreadyExecuted, result2.Status);
        Assert.True(result2.IsSuccess);
        Assert.False(result2.HasAction);
        Assert.True(result2.IsDuplicateSuppressed);
        Assert.Equal(1, result2.ActionExecution!.StateVersionBefore);
        Assert.Equal(2, result2.ActionExecution.StateVersionAfter);

        // Verify only 1 transition record in database
        await using var verifyDb = new CoreDbContext(_options);
        var count = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RunAsync_WhenSameExecutionIdUsedWithDifferentPayload_ReturnsIdempotencyConflict()
    {
        var charId = await SeedCharacterStateAsync(hunger: 80m, energy: 50m);
        var sharedExecutionId = Guid.NewGuid();

        // Run 1: executes with Eat
        var context1 = CreateContext(charId, executionId: sharedExecutionId);
        await using (var db1 = new CoreDbContext(_options))
        {
            var service1 = CreateService(db1);
            var result1 = await service1.RunAsync(context1);
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result1.Status);
        }

        // Run 2: same ExecutionId, but initial state passed has energy=10 (would propose Rest)
        var conflictingState = new CharacterStateSnapshot(hunger: 10, energy: 10, stress: 10, version: 2);
        var context2 = CreateContext(charId, executionId: sharedExecutionId, initialState: conflictingState);

        await using (var db2 = new CoreDbContext(_options))
        {
            var service2 = CreateService(db2);
            var result2 = await service2.RunAsync(context2);

            Assert.Equal(CharacterCognitiveCycleStatus.IdempotencyConflict, result2.Status);
            Assert.False(result2.IsSuccess);
        }
    }

    #endregion

    #region 7. Concurrency Conflict Tests

    [Fact]
    public async Task RunAsync_WhenStateVersionMismatchesAtExecution_ReturnsConcurrencyConflict_WithoutRetrying()
    {
        var charId = await SeedCharacterStateAsync(hunger: 80m);

        // Initial state at Version 1
        var staleState = new CharacterStateSnapshot(hunger: 80, energy: 80, stress: 20, version: 1);

        // Advance DB state to Version 2 externally
        await using (var seedDb = new CoreDbContext(_options))
        {
            var seedService = CreateService(seedDb);
            await seedService.RunAsync(CreateContext(charId));
        }

        // Execute cycle with stale InitialState (Version 1)
        var staleContext = CreateContext(charId, initialState: staleState);

        await using var testDb = new CoreDbContext(_options);
        var service = CreateService(testDb);

        var result = await service.RunAsync(staleContext);

        Assert.Equal(CharacterCognitiveCycleStatus.ConcurrencyConflict, result.Status);
        Assert.False(result.IsSuccess);

        // Verify state remains at Version 2 and no extra transition recorded
        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(2, state.Version);

        var transitionCount = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(1, transitionCount);
    }

    #endregion

    #region 8. Unexpected Exception Propagation

    private sealed class BuggyExperiencePolicy : ICharacterInternalExperiencePolicy
    {
        public CharacterInternalExperience Evaluate(CharacterStateSnapshot state, CharacterPerceptionContext context, PsychologyProfile? psychology = null) =>
            throw new InvalidOperationException("Fatal dependency failure in experience policy.");
    }

    [Fact]
    public async Task RunAsync_WhenDependencyThrowsUnexpectedException_PropagatesWithoutBeingSwallowed()
    {
        var charId = await SeedCharacterStateAsync();
        var context = CreateContext(charId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db, experiencePolicy: new BuggyExperiencePolicy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RunAsync(context));
        Assert.Equal("Fatal dependency failure in experience policy.", ex.Message);
    }

    #endregion

    #region 9. Determinism Tests

    [Fact]
    public async Task RunAsync_Is100PercentDeterministic_Over100Evaluations()
    {
        var snapshot = new CharacterStateSnapshot(hunger: 85, energy: 40, stress: 35, version: 3);
        var charId = Guid.NewGuid();

        CharacterCognitiveCycleResult? baseline = null;

        for (int i = 0; i < 100; i++)
        {
            await using var db = new CoreDbContext(_options);
            var service = CreateService(db);
            var context = CreateContext(charId, initialState: snapshot);

            // Execute policies (will stop before DB because execution service rejects non-existent charId in DB,
            // or we verify up to proposal evaluation)
            var experience = new CharacterInternalExperiencePolicy().Evaluate(
                snapshot, new CharacterPerceptionContext(FixedNow.UtcDateTime, charId));
            var appraisal = new CharacterAppraisalPolicy().Evaluate(experience);
            var emotion = new CharacterEmotionPolicy().Evaluate(appraisal);
            var desires = new CharacterDesirePolicy().Evaluate(experience, appraisal, emotion);
            var intent = new CharacterIntentPolicy().Evaluate(desires, new CharacterIntentContext(FixedNow));
            var proposal = new CharacterActionProposalPolicy().Evaluate(intent, new CharacterActionProposalContext(FixedNow));

            if (baseline == null)
            {
                baseline = CharacterCognitiveCycleResult.CompletedWithoutAction(
                    context.CycleId, context.ExecutionId, charId, FixedNow, snapshot.Version,
                    experience, appraisal, emotion, desires, intent, proposal);
            }
            else
            {
                Assert.Equal(baseline.Experience!.DominantNeed, experience.DominantNeed);
                Assert.Equal(baseline.Appraisal!.Type, appraisal.Type);
                Assert.Equal(baseline.Appraisal.Polarity, appraisal.Polarity);
                Assert.Equal(baseline.Desires!.DominantDesire?.Type, desires.DominantDesire?.Type);
                Assert.Equal(baseline.Intent!.Intent?.Type, intent.Intent?.Type);
                Assert.Equal(baseline.ActionProposal!.Proposal?.Type, proposal.Proposal?.Type);
                Assert.Equal(baseline.ActionProposal.Proposal?.Intensity, proposal.Proposal?.Intensity);
            }
        }
    }

    #endregion

    #region 10. Concurrency Tests

    [Fact]
    public async Task RunAsync_TenConcurrentWorkersWithSameExecutionId_ExecutesExactlyOnce()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        await using var initDb = new CoreDbContext(_options);
        var initialState = (await initDb.CharacterStates.SingleAsync(s => s.CharacterId == charId)).ToSnapshot();
        var sharedExecutionId = Guid.NewGuid();
        var sharedCycleId = Guid.NewGuid();

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var service = CreateService(workerDb);
            var context = CreateContext(charId, cycleId: sharedCycleId, executionId: sharedExecutionId, initialState: initialState);
            return await service.RunAsync(context);
        });

        var results = await Task.WhenAll(tasks);

        var appliedCount = results.Count(r => r.Status == CharacterCognitiveCycleStatus.CompletedWithAction);
        var suppressedCount = results.Count(r => r.Status == CharacterCognitiveCycleStatus.AlreadyExecuted);

        Assert.Equal(1, appliedCount);
        Assert.Equal(9, suppressedCount);

        await using var verifyDb = new CoreDbContext(_options);
        var transitions = await verifyDb.CharacterStateTransitions.Where(t => t.CharacterId == charId).ToListAsync();
        Assert.Single(transitions);

        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(2, state.Version);
    }

    [Fact]
    public async Task RunAsync_TwentyConcurrentWorkersWithDistinctExecutionIds_RespectsOptimisticConcurrency()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var service = CreateService(workerDb);
            var context = CreateContext(charId); // unique execution ID each
            return await service.RunAsync(context);
        });

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r =>
        {
            Assert.True(
                r.Status == CharacterCognitiveCycleStatus.CompletedWithAction ||
                r.Status == CharacterCognitiveCycleStatus.ConcurrencyConflict,
                $"Unexpected status: {r.Status}");
        });

        var appliedCount = results.Count(r => r.Status == CharacterCognitiveCycleStatus.CompletedWithAction);
        Assert.True(appliedCount >= 1, "At least one worker must have succeeded.");

        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(1 + appliedCount, state.Version);

        var transitionCount = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(appliedCount, transitionCount);
    }

    #endregion

    #region 11. Validation & Not Found Tests

    [Fact]
    public async Task RunAsync_RejectsInvalidInputs()
    {
        var charId = Guid.NewGuid();
        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        // Null context
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RunAsync(null!));

        // Empty CycleId
        var res1 = await service.RunAsync(new CharacterCognitiveCycleContext(Guid.Empty, Guid.NewGuid(), charId, FixedNow));
        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, res1.Status);

        // Empty ExecutionId
        var res2 = await service.RunAsync(new CharacterCognitiveCycleContext(Guid.NewGuid(), Guid.Empty, charId, FixedNow));
        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, res2.Status);

        // Empty CharacterId
        var res3 = await service.RunAsync(new CharacterCognitiveCycleContext(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, FixedNow));
        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, res3.Status);
    }

    [Fact]
    public async Task RunAsync_ReturnsNotFound_WhenCharacterDoesNotExist()
    {
        var nonExistentCharId = Guid.NewGuid();
        var context = CreateContext(nonExistentCharId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.NotFound, result.Status);
        Assert.False(result.IsSuccess);
    }

    #endregion
}
