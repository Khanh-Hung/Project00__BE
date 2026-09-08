using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.Safety;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.CognitiveCycle;
using Infrastructure.Services.Safety;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.Safety;

public sealed class CharacterCognitiveCycleSafetyIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    public CharacterCognitiveCycleSafetyIntegrationTests()
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
        decimal comfort = 50m)
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
        IActionSafetyGate? safetyGate,
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
            experiencePolicy: new CharacterInternalExperiencePolicy(),
            appraisalPolicy: new CharacterAppraisalPolicy(),
            emotionPolicy: new CharacterEmotionPolicy(),
            desirePolicy: new CharacterDesirePolicy(),
            intentPolicy: new CharacterIntentPolicy(),
            actionProposalPolicy: new CharacterActionProposalPolicy(),
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance,
            safetyGate: safetyGate
        );
    }

    [Fact]
    public async Task RunAsync_WhenSafetyGateAllows_ExecutesActionAndMutatesState()
    {
        // High hunger (90) triggers SeekFood -> Eat
        var charId = await SeedCharacterStateAsync(hunger: 90m, energy: 70m, stress: 10m);
        var cycleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var context = new CharacterCognitiveCycleContext(cycleId, executionId, charId, FixedNow);

        await using var db = new CoreDbContext(_options);
        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

        var defaultPolicy = new DefaultActionSafetyPolicy();
        var gate = new ActionSafetyGate(stateService, new[] { defaultPolicy }, NullLogger<ActionSafetyGate>.Instance);
        var service = CreateService(db, gate);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.True(result.HasAction);
        Assert.NotNull(result.SafetyDecision);
        Assert.True(result.SafetyDecision.IsAllowed);
        Assert.Equal("ALLOWED", result.SafetyDecision.PolicyCode);
        Assert.NotNull(result.ActionExecution);
        Assert.Equal(CharacterActionExecutionStatus.Applied, result.ActionExecution.Status);

        // State was mutated: Eat reduces hunger
        var updatedState = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.True(updatedState.Hunger < 90m);
        Assert.Equal(2, updatedState.Version);
    }

    [Fact]
    public async Task RunAsync_WhenSafetyGateDenies_NeverExecutesActionAndDoesNotMutateState()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m, energy: 70m, stress: 10m);
        var cycleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var context = new CharacterCognitiveCycleContext(cycleId, executionId, charId, FixedNow);

        await using var db = new CoreDbContext(_options);
        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

        var blockingPolicy = new ConfigurableSafetyPolicy
        {
            Handler = (_, _) => SafetyDecision.Denied("RESTRICTED_ACTION", "Action is currently prohibited by safety guardrails.")
        };

        var trackingExecutionService = new TrackingActionExecutionService();

        var gate = new ActionSafetyGate(stateService, new[] { blockingPolicy }, NullLogger<ActionSafetyGate>.Instance);
        var service = CreateService(db, gate, trackingExecutionService);

        var result = await service.RunAsync(context);

        // Cycle completes without action
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithoutAction, result.Status);
        Assert.False(result.HasAction);
        Assert.NotNull(result.SafetyDecision);
        Assert.False(result.SafetyDecision.IsAllowed);
        Assert.Equal("RESTRICTED_ACTION", result.SafetyDecision.PolicyCode);
        Assert.Equal("Action is currently prohibited by safety guardrails.", result.SafetyDecision.Reason);
        Assert.Null(result.ActionExecution);

        // CRITICAL INVARIANT: Action execution service was NEVER called
        Assert.Equal(0, trackingExecutionService.CallCount);

        // CRITICAL INVARIANT: State was NOT mutated
        var unchangedState = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.Equal(90m, unchangedState.Hunger);
        Assert.Equal(1, unchangedState.Version);
    }

    [Fact]
    public async Task RunAsync_WhenSafetyEvaluationThrows_FailsClosedAndDoesNotMutateState()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m, energy: 70m, stress: 10m);
        var cycleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var context = new CharacterCognitiveCycleContext(cycleId, executionId, charId, FixedNow);

        await using var db = new CoreDbContext(_options);
        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

        var throwingPolicy = new ConfigurableSafetyPolicy
        {
            Handler = (_, _) => throw new InvalidOperationException("Fatal safety rule check exception.")
        };

        var trackingExecutionService = new TrackingActionExecutionService();

        var gate = new ActionSafetyGate(stateService, new[] { throwingPolicy }, NullLogger<ActionSafetyGate>.Instance);
        var service = CreateService(db, gate, trackingExecutionService);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithoutAction, result.Status);
        Assert.False(result.HasAction);
        Assert.NotNull(result.SafetyDecision);
        Assert.False(result.SafetyDecision.IsAllowed);
        Assert.Equal("SAFETY_EVALUATION_FAILED", result.SafetyDecision.PolicyCode);
        Assert.Contains("Fatal safety rule check exception", result.SafetyDecision.Reason);

        Assert.Equal(0, trackingExecutionService.CallCount);

        var unchangedState = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.Equal(90m, unchangedState.Hunger);
        Assert.Equal(1, unchangedState.Version);
    }

    [Fact]
    public async Task Constructor_WhenSafetyGateIsNull_ThrowsArgumentNullException_ProvingMandatoryBoundary()
    {
        await using var db = new CoreDbContext(_options);
        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
        var execService = new CharacterActionExecutionService(transitionService, new CharacterActionExecutionPolicy(), NullLogger<CharacterActionExecutionService>.Instance);

        // Composition contract invariant: Without IActionSafetyGate, construction FAILS and action cannot execute
        var ex = Assert.Throws<ArgumentNullException>(() => new CharacterCognitiveCycleService(
            stateService: stateService,
            experiencePolicy: new CharacterInternalExperiencePolicy(),
            appraisalPolicy: new CharacterAppraisalPolicy(),
            emotionPolicy: new CharacterEmotionPolicy(),
            desirePolicy: new CharacterDesirePolicy(),
            intentPolicy: new CharacterIntentPolicy(),
            actionProposalPolicy: new CharacterActionProposalPolicy(),
            safetyGate: null!,
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance
        ));

        Assert.Equal("safetyGate", ex.ParamName);
    }

    private sealed class ConfigurableSafetyPolicy : IActionSafetyPolicy
    {
        public int Priority { get; set; } = 0;
        public Func<CharacterActionProposal, CharacterSafetyContext, SafetyDecision> Handler { get; set; } = (_, _) => SafetyDecision.Allowed();

        public SafetyDecision Evaluate(CharacterActionProposal proposal, CharacterSafetyContext context) =>
            Handler(proposal, context);
    }

    private sealed class TrackingActionExecutionService : ICharacterActionExecutionService
    {
        public int CallCount { get; private set; }

        public Task<CharacterActionExecutionResult> ExecuteAsync(
            Guid characterId,
            CharacterActionProposal proposal,
            CharacterActionExecutionContext context,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(CharacterActionExecutionResult.Applied(
                context.ExecutionId,
                characterId,
                proposal,
                1,
                2,
                CharacterStateDelta.Zero,
                new CharacterStateSnapshot(version: 2)));
        }
    }
}
