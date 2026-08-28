using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Contracts.Goals;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.AutonomousLoop;

public sealed class CriticalNeedBoundaryTests
{
    private readonly AutonomousDecisionService _decisionService = new(NullLogger<AutonomousDecisionService>.Instance);

    [Theory]
    [InlineData(19, true)]   // Energy 19: Critical (<20) -> Incompatible tasks (Working) filtered, Rest strictly wins
    [InlineData(20, false)]  // Energy 20: Not critical (<20) -> Critical Goal can be pursued
    public async Task Energy_BoundaryAt20_FiltersIncompatibleIntenseTasks(int energyLevel, bool expectRest)
    {
        var charId = Guid.NewGuid();
        var goalSnapshot = new CharacterGoalSnapshot(
            GoalId: Guid.NewGuid(),
            CharacterId: charId,
            Title: "Master Arcane Alchemy",
            GoalType: CharacterGoalType.SkillDevelopment,
            Priority: CharacterGoalPriority.Critical,
            Status: CharacterGoalStatus.Active,
            Progress: 0.1f,
            CurrentValue: 10,
            TargetValue: 100,
            CurrentMilestone: "Brew Potions",
            MilestoneProgress: 0.5f,
            Description: "Study arcane chemistry"
        );

        // Forenoon (10:00) where Scholar would normally Working on alchemy
        var request = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc),
            CurrentLocation: "Arcane Laboratory",
            TimeBucket: "2026-08-28T10:00",
            PersonalityPrompt: "Scholar Alchemist",
            Goals: new[] { goalSnapshot },
            StateSnapshot: new CharacterStateSnapshot(energy: energyLevel, hunger: 20, socialNeed: 20, stress: 10)
        );

        var result = await _decisionService.DecideNextActionAsync(request);

        Assert.Equal(AutonomousDecisionAction.PerformActivity, result.Action);
        Assert.NotNull(result.Candidate);

        if (expectRest)
        {
            Assert.Contains(result.Candidate.ActivityType, new[] { CharacterActivityType.Relaxing, CharacterActivityType.Sleeping, CharacterActivityType.Idle });
            Assert.NotEqual(CharacterActivityType.Working, result.Candidate.ActivityType);
        }
        else
        {
            Assert.Equal(CharacterActivityType.Working, result.Candidate.ActivityType);
        }
    }

    [Theory]
    [InlineData(79, false)] // Hunger 79: High hunger, but critical goal can proceed
    [InlineData(80, false)] // Hunger 80: High hunger threshold
    [InlineData(81, true)]  // Hunger 81: Critical hunger (>80) -> Goal boost strictly capped, Eating strictly wins
    public async Task Hunger_BoundaryAt80_CriticalThresholdForcesEatingOverGoal(int hungerLevel, bool expectEating)
    {
        var charId = Guid.NewGuid();
        var goalSnapshot = new CharacterGoalSnapshot(
            GoalId: Guid.NewGuid(),
            CharacterId: charId,
            Title: "Master Arcane Alchemy",
            GoalType: CharacterGoalType.SkillDevelopment,
            Priority: CharacterGoalPriority.Critical,
            Status: CharacterGoalStatus.Active,
            Progress: 0.1f,
            CurrentValue: 10,
            TargetValue: 100,
            CurrentMilestone: "Brew Potions",
            MilestoneProgress: 0.5f,
            Description: "Study arcane chemistry"
        );

        var request = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc),
            CurrentLocation: "Arcane Laboratory",
            TimeBucket: "2026-08-28T10:00",
            PersonalityPrompt: "Scholar Alchemist",
            Goals: new[] { goalSnapshot },
            StateSnapshot: new CharacterStateSnapshot(energy: 90, hunger: hungerLevel, socialNeed: 20, stress: 10)
        );

        var result = await _decisionService.DecideNextActionAsync(request);

        Assert.Equal(AutonomousDecisionAction.PerformActivity, result.Action);
        Assert.NotNull(result.Candidate);

        if (expectEating)
        {
            Assert.Contains(result.Candidate.ActivityType, new[] { CharacterActivityType.Eating, CharacterActivityType.Cooking });
        }
        else
        {
            Assert.Equal(CharacterActivityType.Working, result.Candidate.ActivityType);
        }
    }

    [Theory]
    [InlineData(29, true)]   // Energy 29: Low energy modifier (< 30) active
    [InlineData(30, false)]  // Energy 30: Normal baseline energy
    public async Task Energy_BoundaryAt30_LowEnergyModifierActivation(int energyLevel, bool expectLowEnergyBoost)
    {
        var charId = Guid.NewGuid();
        var request = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: new DateTime(2026, 8, 28, 15, 0, 0, DateTimeKind.Utc),
            CurrentLocation: "Sanctuary",
            TimeBucket: "2026-08-28T15:00",
            StateSnapshot: new CharacterStateSnapshot(energy: energyLevel, hunger: 20, socialNeed: 20, stress: 10)
        );

        var result = await _decisionService.DecideNextActionAsync(request);

        Assert.Equal(AutonomousDecisionAction.PerformActivity, result.Action);
        Assert.NotNull(result.Candidate);

        if (expectLowEnergyBoost)
        {
            Assert.Contains(result.Candidate.ActivityType, new[] { CharacterActivityType.Relaxing, CharacterActivityType.Sleeping });
        }
        else
        {
            Assert.NotEqual(CharacterActivityType.Sleeping, result.Candidate.ActivityType);
        }
    }
}
