using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Application.Common;
using Application.Contracts.Safety;
using Application.Interfaces;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Services.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.Safety;

public sealed class ActionSafetyGateTests
{
    private readonly NullLogger<ActionSafetyGate> _logger = NullLogger<ActionSafetyGate>.Instance;

    [Fact]
    public async Task EvaluateAsync_NullProposal_ReturnsDeniedWithInvalidAction()
    {
        var stateService = new FakeCharacterStateService();
        var gate = new ActionSafetyGate(stateService, Array.Empty<IActionSafetyPolicy>(), _logger);

        var decision = await gate.EvaluateAsync(Guid.NewGuid(), null!);

        Assert.False(decision.IsAllowed);
        Assert.Equal("INVALID_ACTION", decision.PolicyCode);
        Assert.Equal(0, stateService.GetAsyncCallCount);
    }

    [Fact]
    public async Task EvaluateAsync_CharacterNotFound_ReturnsDeniedWithCharacterNotFound()
    {
        var charId = Guid.NewGuid();
        var stateService = new FakeCharacterStateService { SnapshotToReturn = null };
        var gate = new ActionSafetyGate(stateService, Array.Empty<IActionSafetyPolicy>(), _logger);

        var proposal = new CharacterActionProposal(
            ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, 1);

        var decision = await gate.EvaluateAsync(charId, proposal);

        Assert.False(decision.IsAllowed);
        Assert.Equal("CHARACTER_NOT_FOUND", decision.PolicyCode);
        Assert.Equal(1, stateService.GetAsyncCallCount);
    }

    [Fact]
    public async Task EvaluateAsync_AllPoliciesAllow_ReturnsAllowed()
    {
        var charId = Guid.NewGuid();
        var snapshot = new CharacterStateSnapshot(version: 1);
        var stateService = new FakeCharacterStateService { SnapshotToReturn = snapshot };

        var policy1 = new FakeActionSafetyPolicy(1, (_, _) => SafetyDecision.Allowed());
        var policy2 = new FakeActionSafetyPolicy(2, (_, _) => SafetyDecision.Allowed());

        // Pass in reverse order to verify internal sorting by Priority
        var gate = new ActionSafetyGate(stateService, new[] { policy2, policy1 }, _logger);

        var proposal = new CharacterActionProposal(
            ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, 1);

        var decision = await gate.EvaluateAsync(charId, proposal);

        Assert.True(decision.IsAllowed);
        Assert.Equal("ALLOWED", decision.PolicyCode);

        Assert.Equal(1, policy1.CallCount);
        Assert.Equal(1, policy2.CallCount);
        Assert.Equal(charId, policy1.LastContext?.CharacterId);
        Assert.Same(snapshot, policy1.LastContext?.CharacterState);
    }

    [Fact]
    public async Task EvaluateAsync_PolicyDenies_ReturnsDeniedAndShortCircuits()
    {
        var charId = Guid.NewGuid();
        var snapshot = new CharacterStateSnapshot(version: 1);
        var stateService = new FakeCharacterStateService { SnapshotToReturn = snapshot };

        var policy1 = new FakeActionSafetyPolicy(10, (_, _) => SafetyDecision.Denied("POLICY_BLOCKED", "Harmful action detected"));
        var policy2 = new FakeActionSafetyPolicy(20, (_, _) => SafetyDecision.Allowed());

        var gate = new ActionSafetyGate(stateService, new[] { policy2, policy1 }, _logger);

        var proposal = new CharacterActionProposal(
            ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, 1);

        var decision = await gate.EvaluateAsync(charId, proposal);

        Assert.False(decision.IsAllowed);
        Assert.Equal("POLICY_BLOCKED", decision.PolicyCode);
        Assert.Equal("Harmful action detected", decision.Reason);

        Assert.Equal(1, policy1.CallCount);
        Assert.Equal(0, policy2.CallCount); // Short-circuited!
    }

    [Fact]
    public async Task EvaluateAsync_PolicyThrowsException_FailsClosedWithSafetyEvaluationFailed()
    {
        var charId = Guid.NewGuid();
        var snapshot = new CharacterStateSnapshot(version: 1);
        var stateService = new FakeCharacterStateService { SnapshotToReturn = snapshot };

        var faultyPolicy = new FakeActionSafetyPolicy(1, (_, _) => throw new InvalidOperationException("Unexpected internal engine error."));
        var gate = new ActionSafetyGate(stateService, new[] { faultyPolicy }, _logger);

        var proposal = new CharacterActionProposal(
            ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, 1);

        var decision = await gate.EvaluateAsync(charId, proposal);

        Assert.False(decision.IsAllowed);
        Assert.Equal("SAFETY_EVALUATION_FAILED", decision.PolicyCode);
        Assert.Contains("Unexpected internal engine error", decision.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_StateServiceThrowsException_FailsClosed()
    {
        var charId = Guid.NewGuid();
        var stateService = new FakeCharacterStateService
        {
            ExceptionToThrow = new TimeoutException("Database connection timeout.")
        };

        var gate = new ActionSafetyGate(stateService, Array.Empty<IActionSafetyPolicy>(), _logger);

        var proposal = new CharacterActionProposal(
            ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, 1);

        var decision = await gate.EvaluateAsync(charId, proposal);

        Assert.False(decision.IsAllowed);
        Assert.Equal("SAFETY_EVALUATION_FAILED", decision.PolicyCode);
        Assert.Contains("Database connection timeout", decision.Reason);
    }

    private sealed class FakeCharacterStateService : ICharacterStateService
    {
        public CharacterStateSnapshot? SnapshotToReturn { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public int GetAsyncCallCount { get; private set; }

        public Task<CharacterStateSnapshot?> GetAsync(Guid characterId, CancellationToken ct = default)
        {
            GetAsyncCallCount++;
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            return Task.FromResult(SnapshotToReturn);
        }

        public Task<CharacterStateSnapshot> GetOrCreateInitialStateAsync(Guid characterId, DateTime nowUtc, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<StateTransitionResult> ApplyDeltaAsync(Guid characterId, CharacterStateDelta delta, StateTransitionContext context, DateTime nowUtc, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<StateTransitionResult> EvolveToAsync(Guid characterId, DateTime nowUtc, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeActionSafetyPolicy : IActionSafetyPolicy
    {
        private readonly Func<CharacterActionProposal, CharacterSafetyContext, SafetyDecision> _handler;

        public int Priority { get; }
        public int CallCount { get; private set; }
        public CharacterActionProposal? LastProposal { get; private set; }
        public CharacterSafetyContext? LastContext { get; private set; }

        public FakeActionSafetyPolicy(int priority, Func<CharacterActionProposal, CharacterSafetyContext, SafetyDecision> handler)
        {
            Priority = priority;
            _handler = handler;
        }

        public SafetyDecision Evaluate(CharacterActionProposal proposal, CharacterSafetyContext context)
        {
            CallCount++;
            LastProposal = proposal;
            LastContext = context;
            return _handler(proposal, context);
        }
    }
}
