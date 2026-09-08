using System;
using Application.Contracts.Safety;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Services.Safety;
using Xunit;

namespace Tests.Safety;

public sealed class DefaultActionSafetyPolicyTests
{
    private readonly DefaultActionSafetyPolicy _policy = new();

    [Fact]
    public void Priority_ReturnsZero()
    {
        Assert.Equal(0, _policy.Priority);
    }

    [Fact]
    public void Evaluate_ValidProposalMatchingStateVersion_ReturnsAllowed()
    {
        var proposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.8,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 3
        );

        var snapshot = new CharacterStateSnapshot(version: 3);
        var context = new CharacterSafetyContext(Guid.NewGuid(), snapshot);

        var decision = _policy.Evaluate(proposal, context);

        Assert.True(decision.IsAllowed);
        Assert.Equal("ALLOWED", decision.PolicyCode);
    }

    [Fact]
    public void Evaluate_NullProposal_ThrowsArgumentNullException()
    {
        var snapshot = new CharacterStateSnapshot(version: 1);
        var context = new CharacterSafetyContext(Guid.NewGuid(), snapshot);

        Assert.Throws<ArgumentNullException>(() => _policy.Evaluate(null!, context));
    }

    [Fact]
    public void Evaluate_NullContext_ThrowsArgumentNullException()
    {
        var proposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.5,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        Assert.Throws<ArgumentNullException>(() => _policy.Evaluate(proposal, null!));
    }

    [Fact]
    public void Evaluate_StateVersionMismatch_ReturnsDenied()
    {
        var proposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.5,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 2
        );

        var snapshot = new CharacterStateSnapshot(version: 5);
        var context = new CharacterSafetyContext(Guid.NewGuid(), snapshot);

        var decision = _policy.Evaluate(proposal, context);

        Assert.False(decision.IsAllowed);
        Assert.Equal("STATE_VERSION_MISMATCH", decision.PolicyCode);
        Assert.Contains("does not match authoritative state version", decision.Reason);
    }

    [Fact]
    public void Evaluate_UndefinedActionType_ReturnsDenied()
    {
        var valid = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.5,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        var invalidProposal = valid with { Type = (ActionType)999 };
        var snapshot = new CharacterStateSnapshot(version: 1);
        var context = new CharacterSafetyContext(Guid.NewGuid(), snapshot);

        var decision = _policy.Evaluate(invalidProposal, context);

        Assert.False(decision.IsAllowed);
        Assert.Equal("INVALID_ACTION_TYPE", decision.PolicyCode);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Evaluate_InvalidIntensity_ReturnsDenied(double invalidIntensity)
    {
        var valid = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.5,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        var invalidProposal = valid with { Intensity = invalidIntensity };
        var snapshot = new CharacterStateSnapshot(version: 1);
        var context = new CharacterSafetyContext(Guid.NewGuid(), snapshot);

        var decision = _policy.Evaluate(invalidProposal, context);

        Assert.False(decision.IsAllowed);
        Assert.Equal("INVALID_ACTION_INTENSITY", decision.PolicyCode);
    }
}
