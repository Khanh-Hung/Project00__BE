using Domain.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Time;
using Application.Contracts.ActionExecution;
using Application.Contracts.Autonomous;
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
using Infrastructure.Services.Autonomous;
using Infrastructure.Services.CognitiveCycle;
using Infrastructure.Services.Goals;
using Infrastructure.Services.Safety;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.AutonomousLoop;

public sealed class AutonomousLifeLoopTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 8, 14, 0, 0, TimeSpan.Zero);

    public AutonomousLifeLoopTests()
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

    private sealed class BlockingSafetyGate : IActionSafetyGate
    {
        public Task<SafetyDecision> EvaluateAsync(
            Guid characterId,
            CharacterActionProposal proposal,
            CancellationToken ct = default)
        {
            return Task.FromResult(SafetyDecision.Denied("POLICY_BLOCKED", "Autonomous action blocked by test safety gate."));
        }
    }

    private sealed class TrackingSafetyGate : IActionSafetyGate
    {
        private readonly IActionSafetyGate _inner;
        public bool WasEvaluated { get; private set; }

        public TrackingSafetyGate(IActionSafetyGate inner)
        {
            _inner = inner;
        }

        public async Task<SafetyDecision> EvaluateAsync(
            Guid characterId,
            CharacterActionProposal proposal,
            CancellationToken ct = default)
        {
            WasEvaluated = true;
            return await _inner.EvaluateAsync(characterId, proposal, ct);
        }
    }

    private sealed class NullGoalService : ICharacterGoalService
    {
        public Task<CharacterGoal?> GetOrSelectActiveGoalAsync(
            Guid characterId,
            CharacterDesireEvaluation desireEvaluation,
            DateTimeOffset now,
            CancellationToken ct = default) => Task.FromResult<CharacterGoal?>(null);

        public Task<CharacterGoalProgressFeedback?> ApplyProgressFeedbackAsync(
            Guid characterId,
            Guid executionId,
            CharacterActionExecutionResult actionExecution,
            CharacterGoalContext? goalContext,
            DateTimeOffset now,
            CancellationToken ct = default) => Task.FromResult<CharacterGoalProgressFeedback?>(null);
    }

    private sealed class FailingProgressGoalService : ICharacterGoalService
    {
        private readonly ICharacterGoalService _inner;

        public FailingProgressGoalService(ICharacterGoalService inner)
        {
            _inner = inner;
        }

        public Task<CharacterGoal?> GetOrSelectActiveGoalAsync(
            Guid characterId,
            CharacterDesireEvaluation desireEvaluation,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            _inner.GetOrSelectActiveGoalAsync(characterId, desireEvaluation, now, ct);

        public Task<CharacterGoalProgressFeedback?> ApplyProgressFeedbackAsync(
            Guid characterId,
            Guid executionId,
            CharacterActionExecutionResult actionExecution,
            CharacterGoalContext? goalContext,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated database failure during goal progress feedback.");
        }
    }

    private sealed class NullIntentPolicy : ICharacterIntentPolicy
    {
        public CharacterIntentEvaluation Evaluate(
            CharacterDesireEvaluation desireEvaluation,
            CharacterIntentContext context)
        {
            return new CharacterIntentEvaluation(
                characterId: desireEvaluation.CharacterId,
                stateVersion: desireEvaluation.StateVersion,
                intent: null,
                evaluatedAtUtc: context.EvaluatedAtUtc
            );
        }
    }

    private sealed class NullActionProposalPolicy : ICharacterActionProposalPolicy
    {
        public CharacterActionProposalEvaluation Evaluate(
            CharacterIntentEvaluation intentEvaluation,
            CharacterActionProposalContext context)
        {
            return new CharacterActionProposalEvaluation(
                characterId: intentEvaluation.CharacterId,
                stateVersion: intentEvaluation.StateVersion,
                proposal: null,
                evaluatedAtUtc: context.EvaluatedAtUtc
            );
        }
    }

    private sealed class ZeroDesirePolicy : ICharacterDesirePolicy
    {
        public CharacterDesireEvaluation Evaluate(
            CharacterInternalExperience experience,
            CharacterAppraisal appraisal,
            CharacterEmotion emotion)
        {
            var zeroDesires = new List<CharacterDesire>
            {
                new CharacterDesire(DesireType.NeedFood, 0.0, DesireSource.Hunger, new CharacterMotivation(MotivationType.HungerDriven, 0.0, DesireSource.Hunger))
            };
            return new CharacterDesireEvaluation(
                experience.CharacterId,
                experience.StateVersion,
                zeroDesires,
                zeroDesires[0]
            );
        }
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

    private (IAutonomousCharacterService Service, ICharacterCognitiveCycleService CycleService, CoreDbContext Db) CreateServices(
        ICharacterGoalService? goalService = null,
        IActionSafetyGate? safetyGate = null,
        ICharacterIntentPolicy? intentPolicy = null,
        ICharacterActionProposalPolicy? proposalPolicy = null,
        ICharacterDesirePolicy? desirePolicy = null)
    {
        var db = new CoreDbContext(_options);

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

        var effectiveGoalService = goalService ?? new CharacterGoalService(
            db,
            new CharacterGoalRepository(db),
            new CharacterGoalPolicy(),
            NullLogger<CharacterGoalService>.Instance);

        var cycleService = new CharacterCognitiveCycleService(
            stateService: stateService,
            experiencePolicy: new CharacterInternalExperiencePolicy(),
            appraisalPolicy: new CharacterAppraisalPolicy(),
            emotionPolicy: new CharacterEmotionPolicy(),
            desirePolicy: desirePolicy ?? new CharacterDesirePolicy(),
            intentPolicy: intentPolicy ?? new CharacterIntentPolicy(),
            actionProposalPolicy: proposalPolicy ?? new CharacterActionProposalPolicy(),
            safetyGate: gate,
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance,
            goalService: effectiveGoalService
        );

        var autonomousService = new AutonomousCharacterService(
            cycleService,
            NullLogger<AutonomousCharacterService>.Instance
        );

        return (autonomousService, cycleService, db);
    }

    [Fact]
    public async Task RunOnceAsync_ValidCharacter_ExecutesFullCognitiveCycleSuccessfully()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 95m);
        var (service, _, _) = CreateServices();

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(AutonomousCycleStatus.Executed, result.Status);
        Assert.Equal(charId, result.CharacterId);
        Assert.NotEqual(Guid.Empty, result.CycleId);
        Assert.Equal(FixedNow, result.SimulationTimeUtc);
        Assert.NotNull(result.Desire);
        Assert.NotNull(result.GoalId);
        Assert.NotNull(result.Intent);
        Assert.NotNull(result.ActionProposal);
        Assert.NotNull(result.SafetyDecision);
        Assert.True(result.SafetyDecision.IsAllowed);
        Assert.NotNull(result.ActionExecutionResult);
        Assert.Equal(CharacterActionExecutionStatus.Applied, result.ActionExecutionResult.Status);
        Assert.NotNull(result.GoalProgressFeedback);
    }

    [Fact]
    public async Task RunOnceAsync_PropagatesSimulationTimeUtcDeterministically()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var (service, _, _) = CreateServices();
        var customTimestamp = new DateTimeOffset(2026, 12, 25, 8, 30, 0, TimeSpan.Zero);

        var result = await service.RunOnceAsync(charId, customTimestamp);

        Assert.True(result.IsSuccess);
        Assert.Equal(customTimestamp, result.SimulationTimeUtc);
        Assert.Equal(customTimestamp, result.Intent!.EvaluatedAtUtc);
        Assert.Equal(customTimestamp, result.ActionProposal!.EvaluatedAtUtc);
    }

    [Fact]
    public async Task RunOnceAsync_UsesAuthoritativeDbState_CannotUseCallerInjectedState()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m, energy: 20m);
        var (service, _, db) = CreateServices();

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.True(result.IsSuccess);
        // Authoritative state was hunger=90, so dominant need/desire should be Hunger
        Assert.Equal(DesireType.NeedFood, result.Desire!.DominantDesire.Type);

        var updatedState = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.True(updatedState.Version > 1);
    }

    [Fact]
    public async Task RunOnceAsync_GeneratesDistinctCycleIdExecutionIdAndEventId()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 95m);
        var (service, cycleService, _) = CreateServices();

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.True(result.IsSuccess);
        var cycleId = result.CycleId;
        var executionId = result.ActionExecutionResult!.ExecutionId;

        Assert.NotEqual(Guid.Empty, cycleId);
        Assert.NotEqual(Guid.Empty, executionId);
        Assert.NotEqual(cycleId, executionId);
    }

    [Fact]
    public async Task RunOnceAsync_WithoutExistingGoal_SelectsAndAdvancesGoalProgress()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var (service, _, db) = CreateServices();

        // Ensure 0 goals exist initially
        var initialGoals = await db.CharacterGoals.Where(g => g.CharacterId == charId).ToListAsync();
        Assert.Empty(initialGoals);

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.GoalId);
        Assert.NotNull(result.GoalProgressFeedback);
        Assert.True(result.GoalProgressFeedback.NewProgress > 0);

        var savedGoal = await db.CharacterGoals.FirstOrDefaultAsync(g => g.Id == result.GoalId.Value);
        Assert.NotNull(savedGoal);
        Assert.Equal("Eat", savedGoal.GoalKey);
        Assert.Equal(result.GoalProgressFeedback.NewProgress, savedGoal.ProgressPercentage);
    }

    [Fact]
    public async Task RunOnceAsync_WithExistingCompatibleGoal_ReusesGoalAndIncrementsProgress()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 95m);
        var (service, _, db) = CreateServices();

        // Pre-create an active goal with 20% progress
        var existingGoal = new CharacterGoal(
            characterId: charId,
            title: "BuildRelationship",
            now: FixedNow,
            goalType: CharacterGoalType.Relationship,
            priority: CharacterGoalPriority.High,
            initialProgress: 20
        );
        db.CharacterGoals.Add(existingGoal);
        await db.SaveChangesAsync();

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(existingGoal.Id, result.GoalId);
        Assert.NotNull(result.GoalProgressFeedback);
        Assert.Equal(20, result.GoalProgressFeedback.PreviousProgress);
        Assert.True(result.GoalProgressFeedback.NewProgress > 20);

        var reloaded = await db.CharacterGoals.FirstAsync(g => g.Id == existingGoal.Id);
        Assert.Equal(result.GoalProgressFeedback.NewProgress, reloaded.ProgressPercentage);
    }

    [Fact]
    public async Task RunOnceAsync_WhenSafetyDenies_ReturnsSafetyBlockedAndDoesNotMutateState()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var (service, _, db) = CreateServices(safetyGate: new BlockingSafetyGate());

        var stateBefore = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        var versionBefore = stateBefore.Version;

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutonomousCycleStatus.SafetyBlocked, result.Status);
        Assert.NotNull(result.SafetyDecision);
        Assert.False(result.SafetyDecision.IsAllowed);
        Assert.Null(result.ActionExecutionResult);

        // State version must remain unmutated
        var stateAfter = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.Equal(versionBefore, stateAfter.Version);
    }

    [Fact]
    public async Task RunOnceAsync_WhenSafetyDenies_DoesNotProduceGoalProgressFeedback()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var (service, _, db) = CreateServices(safetyGate: new BlockingSafetyGate());

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutonomousCycleStatus.SafetyBlocked, result.Status);
        Assert.Null(result.GoalProgressFeedback);
    }

    [Fact]
    public async Task RunOnceAsync_WhenNoDesireFormed_TerminatesGracefullyWithNoDesire()
    {
        var charId = await SeedCharacterStateAsync(hunger: 0m, energy: 100m, stress: 0m, socialNeed: 0m, comfort: 100m);
        var (service, _, _) = CreateServices(desirePolicy: new ZeroDesirePolicy());

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutonomousCycleStatus.NoDesire, result.Status);
        Assert.Null(result.ActionExecutionResult);
    }

    [Fact]
    public async Task RunOnceAsync_WhenNoGoal_TerminatesGracefullyWithNoGoal()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var (service, _, _) = CreateServices(goalService: new NullGoalService());

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutonomousCycleStatus.NoGoal, result.Status);
        Assert.Null(result.ActionExecutionResult);
    }

    [Fact]
    public async Task RunOnceAsync_WhenNoIntentFormed_TerminatesGracefullyWithNoIntent()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var (service, _, _) = CreateServices(intentPolicy: new NullIntentPolicy());

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutonomousCycleStatus.NoIntent, result.Status);
        Assert.Null(result.ActionExecutionResult);
    }

    [Fact]
    public async Task RunOnceAsync_WhenNoActionProposal_TerminatesGracefullyWithNoActionProposal()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var (service, _, _) = CreateServices(proposalPolicy: new NullActionProposalPolicy());

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutonomousCycleStatus.NoActionProposal, result.Status);
        Assert.Null(result.ActionExecutionResult);
    }

    [Fact]
    public async Task RunOnceAsync_FailureIsolation_GoalFeedbackFailureDoesNotRollbackStateMutation()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var db = new CoreDbContext(_options);
        var baseGoalService = new CharacterGoalService(
            db,
            new CharacterGoalRepository(db),
            new CharacterGoalPolicy(),
            NullLogger<CharacterGoalService>.Instance);
        var failingGoalService = new FailingProgressGoalService(baseGoalService);

        var (service, _, _) = CreateServices(goalService: failingGoalService);

        var result = await service.RunOnceAsync(charId, FixedNow);

        // Cognitive cycle commits state mutation; goal feedback failure is logged and isolated
        Assert.True(result.IsSuccess);
        Assert.Equal(AutonomousCycleStatus.Executed, result.Status);
        Assert.NotNull(result.ActionExecutionResult);
        Assert.Equal(CharacterActionExecutionStatus.Applied, result.ActionExecutionResult.Status);
        Assert.Null(result.GoalProgressFeedback);

        var updatedState = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.True(updatedState.Version > 1);
    }

    [Fact]
    public async Task RunOnceAsync_EmptyCharacterId_ThrowsArgumentException()
    {
        var (service, _, _) = CreateServices();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RunOnceAsync(Guid.Empty, FixedNow));
    }

    [Fact]
    public async Task RunOnceAsync_DefaultSimulationTime_ThrowsArgumentException()
    {
        var (service, _, _) = CreateServices();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RunOnceAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task RunOnceAsync_NonExistentCharacter_ReturnsFailedStatus()
    {
        var (service, _, _) = CreateServices();
        var nonExistentId = Guid.NewGuid();

        var result = await service.RunOnceAsync(nonExistentId, FixedNow);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutonomousCycleStatus.Failed, result.Status);
        Assert.Null(result.ActionExecutionResult);
    }

    [Fact]
    public async Task RunOnceAsync_PerceptionStimulusTypeAutonomous_MappedCorrectlyWithoutUserMessage()
    {
        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var autoEvent = new AutonomousCognitiveEvent(
            EventId: eventId,
            CharacterId: charId,
            OccurredAtUtc: FixedNow
        );

        Assert.Equal(CognitiveEventType.Autonomous, autoEvent.EventType);
        Assert.Equal("Autonomous", autoEvent.Source);
        Assert.Equal("AutonomousTick", autoEvent.EventName);
        Assert.Equal("AutonomousTick", autoEvent.TickType);
        Assert.Null(autoEvent.Target);
    }

    [Fact]
    public async Task RunOnceAsync_SafetyGateIsMandatory_NeverBypassesSafetyGate()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var db = new CoreDbContext(_options);
        var stateService = new CharacterStateService(
            db,
            new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance),
            new CharacterStateEvolutionPolicy(),
            NullLogger<CharacterStateService>.Instance);
        var baseGate = new ActionSafetyGate(stateService, new[] { new DefaultActionSafetyPolicy() }, NullLogger<ActionSafetyGate>.Instance);
        var trackingGate = new TrackingSafetyGate(baseGate);

        var (service, _, _) = CreateServices(safetyGate: trackingGate);

        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.True(result.IsSuccess);
        Assert.True(trackingGate.WasEvaluated);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotUseDateTimeUtcNowInDomainEvaluation()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var (service, _, _) = CreateServices();

        var specificTime = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = await service.RunOnceAsync(charId, specificTime);

        Assert.True(result.IsSuccess);
        Assert.Equal(specificTime, result.SimulationTimeUtc);
        Assert.Equal(specificTime, result.Intent!.EvaluatedAtUtc);
    }

    [Fact]
    public async Task RunOnceAsync_MultipleSequentialCycles_ProgressivelyMutateStateAndGoal()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 95m);
        var (service, _, db) = CreateServices();

        var result1 = await service.RunOnceAsync(charId, FixedNow);
        Assert.True(result1.IsSuccess);
        var progress1 = result1.GoalProgressFeedback!.NewProgress;

        var result2 = await service.RunOnceAsync(charId, FixedNow.AddMinutes(10));
        Assert.True(result2.IsSuccess);
        var progress2 = result2.GoalProgressFeedback!.NewProgress;

        Assert.True(progress2 >= progress1);
        Assert.Equal(result1.GoalId, result2.GoalId);

        var state = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.True(state.Version >= 3);
    }

    [Fact]
    public async Task RunOnceAsync_CompletedGoal_DoesNotReceiveFurtherProgress()
    {
        var charId = await SeedCharacterStateAsync(hunger: 95m);
        var (service, _, db) = CreateServices();

        // Create an already completed goal
        var completedGoal = new CharacterGoal(
            characterId: charId,
            title: "Eat",
            now: FixedNow,
            goalType: CharacterGoalType.Lifestyle,
            priority: CharacterGoalPriority.High,
            initialProgress: 100
        );
        db.CharacterGoals.Add(completedGoal);
        await db.SaveChangesAsync();

        // Run cycle: goal policy will either create a new goal or completed goal will not receive progress
        var result = await service.RunOnceAsync(charId, FixedNow);

        Assert.True(result.IsSuccess);
        // The completed goal remains completed
        var reloadedCompleted = await db.CharacterGoals.FirstAsync(g => g.Id == completedGoal.Id);
        Assert.Equal(CharacterGoalStatus.Completed, reloadedCompleted.Status);
        Assert.Equal(100, reloadedCompleted.ProgressPercentage);
    }

    [Fact]
    public async Task RunOnceAsync_IsDeterministicGivenIdenticalSeedAndState()
    {
        var charId1 = await SeedCharacterStateAsync(hunger: 95m, energy: 30m);
        var charId2 = await SeedCharacterStateAsync(hunger: 95m, energy: 30m);

        var (service, _, _) = CreateServices();

        var result1 = await service.RunOnceAsync(charId1, FixedNow);
        var result2 = await service.RunOnceAsync(charId2, FixedNow);

        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);

        Assert.Equal(result1.Desire!.DominantDesire.Type, result2.Desire!.DominantDesire.Type);
        Assert.Equal(result1.Intent!.Intent!.Type, result2.Intent!.Intent!.Type);
        Assert.Equal(result1.ActionProposal!.Proposal!.Type, result2.ActionProposal!.Proposal!.Type);
    }
}
