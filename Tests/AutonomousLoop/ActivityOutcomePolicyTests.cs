using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.AutonomousLoop;

public sealed class ActivityOutcomePolicyTests
{
    [Fact]
    public void OutcomeCalculation_Cooking_ReducesHungerAndIncreasesMood()
    {
        var current = new CharacterStateSnapshot(energy: 80, hunger: 60, socialNeed: 40, stress: 30);
        var newState = CharacterActivityOutcomePolicy.ApplyOutcome(current, CharacterActivityType.Cooking);

        Assert.Equal(70, newState.Energy);       // -10
        Assert.Equal(35, newState.Hunger);       // -25
        Assert.Equal(25, newState.Stress);       // -5
        Assert.Equal(CharacterMood.Happy, newState.Mood);
    }

    [Fact]
    public void OutcomeCalculation_Exercising_IncreasesFitnessAndReducesEnergy()
    {
        var current = new CharacterStateSnapshot(energy: 90, hunger: 30, fitness: 50, stress: 40);
        var newState = CharacterActivityOutcomePolicy.ApplyOutcome(current, CharacterActivityType.Exercising);

        Assert.Equal(60, newState.Energy);       // -30
        Assert.Equal(50, newState.Hunger);       // +20
        Assert.Equal(60, newState.Fitness);      // +10
        Assert.Equal(20, newState.Stress);       // -20
        Assert.Equal(CharacterMood.Happy, newState.Mood);
    }

    [Fact]
    public void OutcomeCalculation_ClampsBetweenZeroAndOneHundred()
    {
        // Near bounds
        var current = new CharacterStateSnapshot(energy: 95, hunger: 10, socialNeed: 5, stress: 5);
        var newState = CharacterActivityOutcomePolicy.ApplyOutcome(current, CharacterActivityType.Sleeping);

        Assert.Equal(100, newState.Energy);      // 95 + 60 clamped to 100
        Assert.Equal(30, newState.Hunger);       // 10 + 20 = 30
        Assert.Equal(0, newState.Stress);        // 5 - 30 clamped to 0
    }
}
