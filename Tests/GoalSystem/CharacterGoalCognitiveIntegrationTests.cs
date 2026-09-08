using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Time;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.Goals;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.CognitiveCycle;
using Infrastructure.Services.Goals;
using Infrastructure.Services.Safety;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GoalSystem;

public sealed class CharacterGoalCognitiveIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 8, 14, 0, 0, TimeSpan.Zero);

    public CharacterGoalCognitiveIntegrationTests()
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

    private sealed class TestSystemClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = FixedNow;
    }

    private async Task<Guid> SeedCharacterStateAsync(
        decimal hunger = 50m,
        decimal energy = 50m,
        decimal stress = 50m,
        decimal socialNeed = 90m,
        decimal comfort = 50m)
    {
        var charId = Guid.NewGuid();
        await using var db = new CoreDbContext(_options);
        var state = new CharacterState(
            charId,
            initializedAtUtc: FixedNow.UtcDateTime,
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

    private CharacterCognitiveCycleService CreateCycleService(
        CoreDbContext db,
        ICharacterGoalService? goalService = null,
        IActionSafetyGate? safetyGate = null)
    {
        var transitionService = new CharacterStateTransitionService(
            db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateEvolutionPolicy = new CharacterStateEvolutionPolicy();
        var stateService = new CharacterStateService(
            db, transitionService, stateEvolutionPolicy, NullLogger<CharacterStateService>.Instance);

        var execPolicy = new CharacterActionExecutionPolicy();
        var execService = new CharacterActionExecutionService(
            transitionService, execPolicy, NullLogger<CharacterActionExecutionService>.Instance);

        var gate = safetyGate ?? new ActionSafetyGate(
            stateService,
            new[] { new DefaultActionSafetyPolicy() },
            NullLogger<ActionSafetyGate>.Instance);

        return new CharacterCognitiveCycleService(
            stateService: stateService,
            experiencePolicy: new CharacterInternalExperiencePolicy(),
            appraisalPolicy: new CharacterAppraisalPolicy(),
            emotionPolicy: new CharacterEmotionPolicy(),
            desirePolicy: new CharacterDesirePolicy(),
            intentPolicy: new CharacterIntentPolicy(),
            actionProposalPolicy: new CharacterActionProposalPolicy(),
            safetyGate: gate,
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance,
            goalService: goalService
        );
    }

    [Fact]
    public async Task CognitiveCycle_UsesAuthoritativeGoalContext()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 90m);

        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        var clock = new TestSystemClock();
        var goalService = new CharacterGoalService(repo, new CharacterGoalPolicy(), clock, NullLogger<CharacterGoalService>.Instance);
        var cycleService = CreateCycleService(db, goalService);

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: FixedNow
        );

        var result = await cycleService.RunAsync(context);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.GoalContext);
        Assert.Equal("BuildRelationship", result.GoalContext.GoalKey);
        Assert.Equal(CharacterGoalStatus.Active, result.GoalContext.Status);
    }

    [Fact]
    public async Task CognitiveCycle_CannotUseCallerInjectedGoalState()
    {
        var charId = await SeedCharacterStateAsync();

        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        var clock = new TestSystemClock();
        var goalService = new CharacterGoalService(repo, new CharacterGoalPolicy(), clock, NullLogger<CharacterGoalService>.Instance);
        var cycleService = CreateCycleService(db, goalService);

        // Caller attempts to inject goal context
        var injectedGoal = new CharacterGoalContext(
            GoalId: Guid.NewGuid(),
            GoalKey: "InjectedMaliciousGoal",
            Status: CharacterGoalStatus.Active,
            Priority: 999,
            Progress: 100
        );

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: FixedNow,
            GoalContext: injectedGoal
        );

        var result = await cycleService.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, result.Status);
        Assert.Contains("GoalContext cannot be pre-populated by caller", result.Message);
    }

    [Fact]
    public void CognitiveCycle_GoalCanInfluenceIntent()
    {
        var charId = Guid.NewGuid();
        var motivation = new CharacterMotivation(MotivationType.ConnectionDriven, 0.7, DesireSource.SocialNeed);
        var desire = new CharacterDesire(DesireType.NeedSocialConnection, 0.7, DesireSource.SocialNeed, motivation);
        var desireEval = new CharacterDesireEvaluation(charId, 1, new[] { desire }, desire);

        var policy = new CharacterIntentPolicy();

        // 1. Without goal
        var intentWithoutGoal = policy.Evaluate(desireEval, new CharacterIntentContext(FixedNow));
        Assert.NotNull(intentWithoutGoal.Intent);
        Assert.Equal(0.7, intentWithoutGoal.Intent.Intensity, 3);

        // 2. With active aligned goal
        var goalContext = new CharacterGoalContext(Guid.NewGuid(), "BuildRelationship", CharacterGoalStatus.Active, 80, 20);
        var intentWithGoal = policy.Evaluate(desireEval, new CharacterIntentContext(FixedNow, goalContext));
        Assert.NotNull(intentWithGoal.Intent);
        // Intensity is boosted when aligned with goal
        Assert.True(intentWithGoal.Intent.Intensity > intentWithoutGoal.Intent.Intensity);
    }

    [Fact]
    public void CognitiveCycle_GoalCanInfluenceActionProposal()
    {
        var charId = Guid.NewGuid();
        var intent = new CharacterIntent(IntentType.SeekSocialConnection, 0.7, DesireType.NeedSocialConnection, MotivationType.ConnectionDriven, 1);
        var intentEval = new CharacterIntentEvaluation(charId, 1, intent, FixedNow.UtcDateTime);

        var policy = new CharacterActionProposalPolicy();

        // 1. Without goal
        var proposalWithoutGoal = policy.Evaluate(intentEval, new CharacterActionProposalContext(FixedNow));
        Assert.NotNull(proposalWithoutGoal.Proposal);
        Assert.Equal(0.7, proposalWithoutGoal.Proposal.Intensity, 3);

        // 2. With active aligned goal
        var goalContext = new CharacterGoalContext(Guid.NewGuid(), "BuildRelationship", CharacterGoalStatus.Active, 80, 20);
        var proposalWithGoal = policy.Evaluate(intentEval, new CharacterActionProposalContext(FixedNow, goalContext));
        Assert.NotNull(proposalWithGoal.Proposal);
        // Intensity is reinforced when aligned with goal
        Assert.True(proposalWithGoal.Proposal.Intensity > proposalWithoutGoal.Proposal.Intensity);
    }

    [Fact]
    public async Task CognitiveCycle_DoesNotMutateGoalBeforeActionExecutionUnlessExplicitlyRequired()
    {
        var charId = await SeedCharacterStateAsync();

        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        var clock = new TestSystemClock();
        var goalService = new CharacterGoalService(repo, new CharacterGoalPolicy(), clock, NullLogger<CharacterGoalService>.Instance);

        // Safety gate that always blocks actions
        var blockingSafetyGate = new FakeBlockingSafetyGate("TEST_SAFETY_BLOCK", "Test safety block");
        var cycleService = CreateCycleService(db, goalService, safetyGate: blockingSafetyGate);

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: FixedNow
        );

        var result = await cycleService.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithoutAction, result.Status);
        Assert.Null(result.GoalFeedback);

        // Goal progress in DB remains 0
        if (result.GoalContext != null)
        {
            var goalInDb = await repo.GetByIdAsync(result.GoalContext.GoalId);
            Assert.NotNull(goalInDb);
            Assert.Equal(0f, goalInDb.Progress);
        }
    }

    [Fact]
    public async Task ActionExecution_CanProduceGoalProgressUpdate()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 90m);

        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        var clock = new TestSystemClock();
        var goalService = new CharacterGoalService(repo, new CharacterGoalPolicy(), clock, NullLogger<CharacterGoalService>.Instance);
        var cycleService = CreateCycleService(db, goalService);

        var executionId = Guid.NewGuid();
        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: executionId,
            CharacterId: charId,
            TriggeredAtUtc: FixedNow
        );

        var result = await cycleService.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.NotNull(result.GoalFeedback);
        Assert.Equal(0, result.GoalFeedback.PreviousProgress);
        Assert.True(result.GoalFeedback.NewProgress > 0);
        Assert.False(result.GoalFeedback.IsDuplicateExecution);

        var goalInDb = await repo.GetByIdAsync(result.GoalFeedback.GoalId);
        Assert.NotNull(goalInDb);
        Assert.Equal(result.GoalFeedback.NewProgress, (int)goalInDb.Progress);
    }

    [Fact]
    public async Task GoalProgressUpdate_IsIdempotent()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "BuildRelationship", initialStatus: CharacterGoalStatus.Active, initialProgress: 20);

        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        await repo.AddAsync(goal);

        var clock = new TestSystemClock();
        var goalService = new CharacterGoalService(repo, new CharacterGoalPolicy(), clock, NullLogger<CharacterGoalService>.Instance);

        var executionId = Guid.NewGuid();
        var actionExec = new CharacterActionExecutionResult(
            ExecutionId: executionId,
            CharacterId: charId,
            Status: CharacterActionExecutionStatus.Applied,
            ActionType: ActionType.Socialize,
            Intensity: 0.8,
            SourceIntent: IntentType.SeekSocialConnection,
            Motivation: MotivationType.ConnectionDriven,
            StateVersionBefore: 1,
            StateVersionAfter: 2,
            AppliedDelta: CharacterStateDelta.Zero
        );

        var goalContext = new CharacterGoalContext(goal.Id, goal.GoalKey, goal.Status, (int)goal.Priority, (int)goal.Progress);

        // First application
        var feedback1 = await goalService.ApplyProgressFeedbackAsync(charId, executionId, actionExec, goalContext, FixedNow);
        Assert.NotNull(feedback1);
        Assert.False(feedback1.IsDuplicateExecution);
        Assert.Equal(45, feedback1.NewProgress);

        // Second application with same ExecutionId -> Idempotent response
        var feedback2 = await goalService.ApplyProgressFeedbackAsync(charId, executionId, actionExec, goalContext, FixedNow);
        Assert.NotNull(feedback2);
        Assert.True(feedback2.IsDuplicateExecution);
        Assert.Equal(feedback1.NewProgress, feedback2.NewProgress);

        var goalInDb = await repo.GetByIdAsync(goal.Id);
        Assert.NotNull(goalInDb);
        Assert.Equal(45f, goalInDb.Progress); // Did not double-increment
    }

    [Fact]
    public async Task GoalProgressUpdate_ConcurrentUpdateDoesNotSilentlyOverwriteWinner()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "BuildRelationship", initialStatus: CharacterGoalStatus.Active, initialProgress: 0);

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterGoalRepository(db);
            await repo.AddAsync(goal);
        }

        // Two DbContext instances reading the same goal version
        using var db1 = new CoreDbContext(_options);
        using var db2 = new CoreDbContext(_options);

        var repo1 = new CharacterGoalRepository(db1);
        var repo2 = new CharacterGoalRepository(db2);

        var g1 = await repo1.GetByIdAsync(goal.Id);
        var g2 = await repo2.GetByIdAsync(goal.Id);

        Assert.NotNull(g1);
        Assert.NotNull(g2);
        Assert.Equal(g1.Version, g2.Version);

        // Worker 1 updates progress and commits successfully
        g1.UpdateProgress(25, FixedNow);
        await repo1.UpdateAsync(g1);

        // Worker 2 attempts to commit with stale version -> Must throw DbUpdateConcurrencyException
        g2.UpdateProgress(30, FixedNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => repo2.UpdateAsync(g2));
    }

    [Fact]
    public async Task CompletedGoalCannotReceiveFurtherProgress()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "BuildRelationship", initialStatus: CharacterGoalStatus.Active, initialProgress: 90);
        goal.Complete();

        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        await repo.AddAsync(goal);

        var clock = new TestSystemClock();
        var goalService = new CharacterGoalService(repo, new CharacterGoalPolicy(), clock, NullLogger<CharacterGoalService>.Instance);

        var executionId = Guid.NewGuid();
        var actionExec = new CharacterActionExecutionResult(
            ExecutionId: executionId,
            CharacterId: charId,
            Status: CharacterActionExecutionStatus.Applied,
            ActionType: ActionType.Socialize,
            Intensity: 0.8,
            SourceIntent: IntentType.SeekSocialConnection,
            Motivation: MotivationType.ConnectionDriven,
            StateVersionBefore: 1,
            StateVersionAfter: 2,
            AppliedDelta: CharacterStateDelta.Zero
        );

        var goalContext = new CharacterGoalContext(goal.Id, goal.GoalKey, goal.Status, (int)goal.Priority, (int)goal.Progress);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            goalService.ApplyProgressFeedbackAsync(charId, executionId, actionExec, goalContext, FixedNow));
    }

    [Fact]
    public async Task GoalId_IsDistinctFromExecutionId()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 90m);

        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        var clock = new TestSystemClock();
        var goalService = new CharacterGoalService(repo, new CharacterGoalPolicy(), clock, NullLogger<CharacterGoalService>.Instance);
        var cycleService = CreateCycleService(db, goalService);

        var executionId = Guid.NewGuid();
        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: executionId,
            CharacterId: charId,
            TriggeredAtUtc: FixedNow
        );

        var result = await cycleService.RunAsync(context);

        Assert.NotNull(result.GoalContext);
        Assert.NotEqual(executionId, result.GoalContext.GoalId);
        Assert.NotEqual(context.CycleId, result.GoalContext.GoalId);
    }

    [Fact]
    public async Task GoalMutation_DoesNotReuseEventIdAsGoalId()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 90m);

        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        var clock = new TestSystemClock();
        var goalService = new CharacterGoalService(repo, new CharacterGoalPolicy(), clock, NullLogger<CharacterGoalService>.Instance);
        var cycleService = CreateCycleService(db, goalService);

        var eventId = Guid.NewGuid();
        var cognitiveEvent = new UserMessageCognitiveEvent(
            EventId: eventId,
            CharacterId: charId,
            OccurredAtUtc: FixedNow,
            Message: "Hello character!"
        );

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: FixedNow,
            Event: cognitiveEvent
        );

        var result = await cycleService.RunAsync(context);

        Assert.NotNull(result.GoalContext);
        Assert.NotEqual(eventId, result.GoalContext.GoalId);
    }

    [Fact]
    public async Task GoalRetrievalFailure_DoesNotInjectCallerGoalState()
    {
        var charId = await SeedCharacterStateAsync();

        using var db = new CoreDbContext(_options);
        var failingGoalService = new FailingGoalService();
        var cycleService = CreateCycleService(db, failingGoalService);

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: FixedNow
        );

        var result = await cycleService.RunAsync(context);

        // Degradation is graceful: goalContext falls back to null, no caller injection
        Assert.True(result.IsSuccess);
        Assert.Null(result.GoalContext);
    }

    [Fact]
    public async Task GoalPersistenceFailure_DoesNotMutateUnrelatedSubsystems()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 90m);

        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        var clock = new TestSystemClock();
        // Service where progress persistence fails
        var failingPersistenceGoalService = new FailingPersistenceGoalService(repo, new CharacterGoalPolicy(), clock);
        var cycleService = CreateCycleService(db, failingPersistenceGoalService);

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: FixedNow
        );

        var result = await cycleService.RunAsync(context);

        // Cognitive cycle succeeds with action; goal failure does not crash or roll back character state mutation
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.NotNull(result.ActionExecution);
        Assert.Equal(CharacterActionExecutionStatus.Applied, result.ActionExecution.Status);

        // CharacterState was mutated despite goal feedback persistence failure
        var state = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.True(state.Version > 1);
    }

    [Fact]
    public async Task SameInputAndSameGoalState_ProducesSameGoalSelection()
    {
        var charId = Guid.NewGuid();
        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        var clock = new TestSystemClock();
        var goalService = new CharacterGoalService(repo, new CharacterGoalPolicy(), clock, NullLogger<CharacterGoalService>.Instance);

        var motivation = new CharacterMotivation(MotivationType.ConnectionDriven, 0.8, DesireSource.SocialNeed);
        var desire = new CharacterDesire(DesireType.NeedSocialConnection, 0.8, DesireSource.SocialNeed, motivation);
        var desireEval = new CharacterDesireEvaluation(charId, 1, new[] { desire }, desire);

        var goal1 = await goalService.GetOrSelectActiveGoalAsync(charId, desireEval, FixedNow);
        var goal2 = await goalService.GetOrSelectActiveGoalAsync(charId, desireEval, FixedNow);

        Assert.NotNull(goal1);
        Assert.NotNull(goal2);
        Assert.Equal(goal1.Id, goal2.Id);
        Assert.Equal(goal1.Title, goal2.Title);
    }

    [Fact]
    public async Task RepeatedEvaluation_DoesNotCreateDuplicateGoals()
    {
        var charId = Guid.NewGuid();
        using var db = new CoreDbContext(_options);
        var repo = new CharacterGoalRepository(db);
        var clock = new TestSystemClock();
        var goalService = new CharacterGoalService(repo, new CharacterGoalPolicy(), clock, NullLogger<CharacterGoalService>.Instance);

        var motivation = new CharacterMotivation(MotivationType.ConnectionDriven, 0.8, DesireSource.SocialNeed);
        var desire = new CharacterDesire(DesireType.NeedSocialConnection, 0.8, DesireSource.SocialNeed, motivation);
        var desireEval = new CharacterDesireEvaluation(charId, 1, new[] { desire }, desire);

        var goals = new List<CharacterGoal>();
        for (int i = 0; i < 5; i++)
        {
            var g = await goalService.GetOrSelectActiveGoalAsync(charId, desireEval, FixedNow);
            Assert.NotNull(g);
            goals.Add(g);
        }

        // All 5 evaluations reuse the exact same goal
        var distinctIds = goals.Select(g => g.Id).Distinct().ToList();
        Assert.Single(distinctIds);

        // Database has exactly 1 goal
        var count = await db.CharacterGoals.CountAsync(g => g.CharacterId == charId);
        Assert.Equal(1, count);
    }

    private sealed class FakeBlockingSafetyGate : IActionSafetyGate
    {
        private readonly string _policyCode;
        private readonly string _reason;

        public FakeBlockingSafetyGate(string policyCode, string reason)
        {
            _policyCode = policyCode;
            _reason = reason;
        }

        public Task<SafetyDecision> EvaluateAsync(
            Guid characterId,
            CharacterActionProposal proposal,
            CancellationToken ct = default)
        {
            return Task.FromResult(SafetyDecision.Denied(_policyCode, _reason));
        }
    }

    private sealed class FailingGoalService : ICharacterGoalService
    {
        public Task<CharacterGoal?> GetOrSelectActiveGoalAsync(
            Guid characterId,
            CharacterDesireEvaluation desireEvaluation,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Goal retrieval service failure simulation.");
        }

        public Task<CharacterGoalProgressFeedback?> ApplyProgressFeedbackAsync(
            Guid characterId,
            Guid executionId,
            CharacterActionExecutionResult actionExecution,
            CharacterGoalContext? goalContext,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            return Task.FromResult<CharacterGoalProgressFeedback?>(null);
        }
    }

    private sealed class FailingPersistenceGoalService : ICharacterGoalService
    {
        private readonly CharacterGoalService _inner;

        public FailingPersistenceGoalService(ICharacterGoalRepository repo, ICharacterGoalPolicy policy, ISystemClock clock)
        {
            _inner = new CharacterGoalService(repo, policy, clock, NullLogger<CharacterGoalService>.Instance);
        }

        public Task<CharacterGoal?> GetOrSelectActiveGoalAsync(
            Guid characterId,
            CharacterDesireEvaluation desireEvaluation,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            return _inner.GetOrSelectActiveGoalAsync(characterId, desireEvaluation, now, ct);
        }

        public Task<CharacterGoalProgressFeedback?> ApplyProgressFeedbackAsync(
            Guid characterId,
            Guid executionId,
            CharacterActionExecutionResult actionExecution,
            CharacterGoalContext? goalContext,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Goal persistence failed during feedback attachment simulation.");
        }
    }
}
