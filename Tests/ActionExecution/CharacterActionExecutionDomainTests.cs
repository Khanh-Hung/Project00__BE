using System;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.ActionExecution;

public sealed class CharacterActionExecutionDomainTests
{
    private readonly CharacterActionExecutionPolicy _policy = new();

    #region 1. Mapping Tests

    [Theory]
    [InlineData(ActionType.Eat, -30.0, 0.0, 0.0, 0.0, 0.0, 0.0)]
    [InlineData(ActionType.Rest, 0.0, 30.0, 0.0, 0.0, 0.0, 0.0)]
    [InlineData(ActionType.ReduceStress, 0.0, 0.0, 0.0, -25.0, 0.0, 0.0)]
    [InlineData(ActionType.Socialize, 0.0, 0.0, 0.0, 0.0, -25.0, 0.0)]
    [InlineData(ActionType.SeekComfort, 0.0, 0.0, 0.0, 0.0, 0.0, 25.0)]
    [InlineData(ActionType.SeekSafety, 0.0, 0.0, 0.0, -20.0, 0.0, 0.0)]
    public void CalculateDelta_ProducesExpectedDelta_ForFullIntensity(
        ActionType actionType,
        decimal expectedHunger,
        decimal expectedEnergy,
        decimal expectedMood,
        decimal expectedStress,
        decimal expectedSocialNeed,
        decimal expectedComfort)
    {
        var proposal = new CharacterActionProposal(
            type: actionType,
            intensity: 1.0,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        var delta = _policy.CalculateDelta(proposal);

        Assert.Equal(expectedHunger, delta.HungerDelta);
        Assert.Equal(expectedEnergy, delta.EnergyDelta);
        Assert.Equal(expectedMood, delta.MoodDelta);
        Assert.Equal(expectedStress, delta.StressDelta);
        Assert.Equal(expectedSocialNeed, delta.SocialNeedDelta);
        Assert.Equal(expectedComfort, delta.ComfortDelta);
    }

    #endregion

    #region 2. Intensity Scaling Tests

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, -7.5)]
    [InlineData(0.5, -15.0)]
    [InlineData(0.75, -22.5)]
    [InlineData(1.0, -30.0)]
    public void CalculateDelta_ScalesLinearlyWithIntensity_ForEatAction(double intensity, decimal expectedHungerDelta)
    {
        var proposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: intensity,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        var delta = _policy.CalculateDelta(proposal);

        Assert.Equal(expectedHungerDelta, delta.HungerDelta);
        Assert.Equal(0m, delta.EnergyDelta);
        Assert.Equal(0m, delta.StressDelta);
        Assert.Equal(0m, delta.SocialNeedDelta);
        Assert.Equal(0m, delta.ComfortDelta);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 7.5)]
    [InlineData(0.5, 15.0)]
    [InlineData(0.75, 22.5)]
    [InlineData(1.0, 30.0)]
    public void CalculateDelta_ScalesLinearlyWithIntensity_ForRestAction(double intensity, decimal expectedEnergyDelta)
    {
        var proposal = new CharacterActionProposal(
            type: ActionType.Rest,
            intensity: intensity,
            sourceIntent: IntentType.SeekRest,
            motivation: MotivationType.RestorationDriven,
            stateVersion: 1
        );

        var delta = _policy.CalculateDelta(proposal);

        Assert.Equal(expectedEnergyDelta, delta.EnergyDelta);
        Assert.Equal(0m, delta.HungerDelta);
    }

    #endregion

    #region 3. Invariant & Null Input Tests

    [Fact]
    public void CalculateDelta_ThrowsArgumentNullException_OnNullProposal()
    {
        Assert.Throws<ArgumentNullException>(() => _policy.CalculateDelta(null!));
    }

    [Fact]
    public void CalculateDelta_NeverMutatesProposal()
    {
        var proposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.8,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 5
        );

        var delta = _policy.CalculateDelta(proposal);

        Assert.NotNull(delta);
        Assert.Equal(ActionType.Eat, proposal.Type);
        Assert.Equal(0.8, proposal.Intensity);
        Assert.Equal(IntentType.SeekFood, proposal.SourceIntent);
        Assert.Equal(MotivationType.HungerDriven, proposal.Motivation);
        Assert.Equal(5, proposal.StateVersion);
    }

    [Fact]
    public void CalculateDelta_Is100PercentDeterministic_Over100Evaluations()
    {
        var proposal = new CharacterActionProposal(
            type: ActionType.ReduceStress,
            intensity: 0.65,
            sourceIntent: IntentType.ReduceStress,
            motivation: MotivationType.StressReliefDriven,
            stateVersion: 3
        );

        var baseline = _policy.CalculateDelta(proposal);

        for (int i = 0; i < 100; i++)
        {
            var next = _policy.CalculateDelta(proposal);
            Assert.Equal(baseline.HungerDelta, next.HungerDelta);
            Assert.Equal(baseline.EnergyDelta, next.EnergyDelta);
            Assert.Equal(baseline.StressDelta, next.StressDelta);
            Assert.Equal(baseline.SocialNeedDelta, next.SocialNeedDelta);
            Assert.Equal(baseline.ComfortDelta, next.ComfortDelta);
        }
    }

    #endregion

    #region 4. Canonical SourceId & Fingerprint Tests

    [Fact]
    public void CreateActionProposalSourceId_CapturesAllSemanticProperties()
    {
        var proposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.75,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 4
        );

        var sourceId = Domain.Common.CanonicalTransitionFingerprint.CreateActionProposalSourceId(proposal);

        Assert.Equal("ActionProposal:Eat:0.7500:SeekFood:HungerDriven:4", sourceId);
    }

    [Fact]
    public void CreateActionProposalSourceId_DifferentiatesChangesInAnyProperty()
    {
        var baseProposal = new CharacterActionProposal(
            ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, 1);

        var diffIntensity = new CharacterActionProposal(
            ActionType.Eat, 0.6, IntentType.SeekFood, MotivationType.HungerDriven, 1);

        var diffVersion = new CharacterActionProposal(
            ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, 2);

        var diffAction = new CharacterActionProposal(
            ActionType.Rest, 0.5, IntentType.SeekRest, MotivationType.RestorationDriven, 1);

        var baseSourceId = Domain.Common.CanonicalTransitionFingerprint.CreateActionProposalSourceId(baseProposal);
        var diffIntensitySourceId = Domain.Common.CanonicalTransitionFingerprint.CreateActionProposalSourceId(diffIntensity);
        var diffVersionSourceId = Domain.Common.CanonicalTransitionFingerprint.CreateActionProposalSourceId(diffVersion);
        var diffActionSourceId = Domain.Common.CanonicalTransitionFingerprint.CreateActionProposalSourceId(diffAction);

        Assert.NotEqual(baseSourceId, diffIntensitySourceId);
        Assert.NotEqual(baseSourceId, diffVersionSourceId);
        Assert.NotEqual(baseSourceId, diffActionSourceId);
    }

    #endregion
}
