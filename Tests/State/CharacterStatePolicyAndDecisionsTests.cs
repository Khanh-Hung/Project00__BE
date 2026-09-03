using Domain.Enums;
using Domain.Policies;
using Xunit;

namespace Tests.State;

public sealed class CharacterStatePolicyAndDecisionsTests
{
    [Fact]
    public void ActivityOutcomePolicy_ProducesExpectedDeltas()
    {
        var sleepDelta = CharacterActivityOutcomeStatePolicy.CalculateOutcomeDelta(CharacterActivityType.Sleeping);
        Assert.True(sleepDelta.EnergyDelta > 0);
        Assert.True(sleepDelta.StressDelta < 0);

        var eatDelta = CharacterActivityOutcomeStatePolicy.CalculateOutcomeDelta(CharacterActivityType.Eating);
        Assert.True(eatDelta.HungerDelta < 0);
        Assert.True(eatDelta.EnergyDelta > 0);

        var socialDelta = CharacterActivityOutcomeStatePolicy.CalculateOutcomeDelta(CharacterActivityType.Socializing);
        Assert.True(socialDelta.SocialNeedDelta < 0);
        Assert.True(socialDelta.MoodDelta > 0);
    }

    [Fact]
    public void NeedsDecisionPolicy_EvaluatesThresholdsCorrectly()
    {
        // Low energy boosts rest & suppresses exertion
        int restMod = CharacterNeedsDecisionPolicy.EvaluateEnergyModifier(15m, CharacterActivityType.Sleeping);
        int exerciseMod = CharacterNeedsDecisionPolicy.EvaluateEnergyModifier(15m, CharacterActivityType.Exercising);
        Assert.Equal(300, restMod);
        Assert.Equal(-250, exerciseMod);

        // High hunger boosts eating
        int foodMod = CharacterNeedsDecisionPolicy.EvaluateHungerModifier(85m, CharacterActivityType.Eating);
        Assert.Equal(300, foodMod);

        // High social need boosts socializing
        int socialMod = CharacterNeedsDecisionPolicy.EvaluateSocialNeedModifier(80m, CharacterActivityType.Socializing);
        Assert.Equal(250, socialMod);

        // High stress boosts relaxation & penalizes work
        int relaxMod = CharacterNeedsDecisionPolicy.EvaluateStressModifier(85m, CharacterActivityType.Relaxing);
        int workMod = CharacterNeedsDecisionPolicy.EvaluateStressModifier(85m, CharacterActivityType.Working);
        Assert.Equal(250, relaxMod);
        Assert.Equal(-200, workMod);

        // Low comfort boosts relaxation/rest
        int comfortMod = CharacterNeedsDecisionPolicy.EvaluateComfortModifier(15m, CharacterActivityType.Relaxing);
        Assert.Equal(150, comfortMod);
    }
}
