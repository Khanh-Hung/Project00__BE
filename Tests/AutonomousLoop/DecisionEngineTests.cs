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

public sealed class DecisionEngineTests
{
    private readonly AutonomousDecisionService _decisionService = new(NullLogger<AutonomousDecisionService>.Instance);

    [Fact]
    public async Task GoalRelevance_PrioritizesGoalMatchingActivity()
    {
        var charId = Guid.NewGuid();
        var goalSnapshot = new CharacterGoalSnapshot(
            GoalId: Guid.NewGuid(),
            CharacterId: charId,
            Title: "Master Arcane Alchemy",
            GoalType: CharacterGoalType.SkillDevelopment,
            Priority: CharacterGoalPriority.High,
            Status: CharacterGoalStatus.Active,
            Progress: 0.1f,
            CurrentValue: 10,
            TargetValue: 100,
            CurrentMilestone: "Brew Potions",
            MilestoneProgress: 0.5f,
            Description: "Study arcane chemistry and brew master recipes"
        );

        var request = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc), // Forenoon (10:00)
            CurrentLocation: "Arcane Laboratory",
            TimeBucket: "2026-08-28T10:00",
            PersonalityPrompt: "Scholar Alchemist",
            Goals: new[] { goalSnapshot },
            StateSnapshot: new CharacterStateSnapshot(energy: 80, hunger: 20, socialNeed: 20, stress: 10)
        );

        var result = await _decisionService.DecideNextActionAsync(request);

        Assert.Equal(AutonomousDecisionAction.PerformActivity, result.Action);
        Assert.NotNull(result.Candidate);
        Assert.Equal(goalSnapshot.GoalId, result.Candidate.GoalId);
        Assert.True(result.Candidate.GoalRelevance.HasValue && result.Candidate.GoalRelevance.Value > 0.05f);
        Assert.Contains(result.Candidate.ActivityType, new[] { CharacterActivityType.Working, CharacterActivityType.Reading, CharacterActivityType.Cooking });
    }

    [Fact]
    public async Task StateNeeds_LowEnergy_TriggersRestOrSleep_EvenWithActiveGoal()
    {
        var charId = Guid.NewGuid();
        var goalSnapshot = new CharacterGoalSnapshot(
            GoalId: Guid.NewGuid(),
            CharacterId: charId,
            Title: "Conquer Mountain Peak",
            GoalType: CharacterGoalType.Exploration,
            Priority: CharacterGoalPriority.High,
            Status: CharacterGoalStatus.Active,
            Progress: 0.1f,
            CurrentValue: 10,
            TargetValue: 100,
            CurrentMilestone: "Scout Basecamp",
            MilestoneProgress: 0.2f
        );

        // Exhausted character (Energy = 10) in the afternoon
        var request = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: new DateTime(2026, 8, 28, 15, 0, 0, DateTimeKind.Utc),
            CurrentLocation: "Mountain Trail",
            TimeBucket: "2026-08-28T15:00",
            Goals: new[] { goalSnapshot },
            StateSnapshot: new CharacterStateSnapshot(energy: 10, hunger: 30, socialNeed: 20, stress: 20)
        );

        var result = await _decisionService.DecideNextActionAsync(request);

        Assert.Equal(AutonomousDecisionAction.PerformActivity, result.Action);
        Assert.NotNull(result.Candidate);
        // Rest takes precedence over intense exploration when exhausted
        Assert.Contains(result.Candidate.ActivityType, new[] { CharacterActivityType.Relaxing, CharacterActivityType.Sleeping, CharacterActivityType.Idle });
    }

    [Fact]
    public async Task StateNeeds_HighHunger_TriggersEatingOrCooking()
    {
        var charId = Guid.NewGuid();
        // Hungry character (Hunger = 85) at 19:00 (Evening)
        var request = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: new DateTime(2026, 8, 28, 19, 0, 0, DateTimeKind.Utc),
            CurrentLocation: "Kitchen & Hearth",
            TimeBucket: "2026-08-28T19:00",
            StateSnapshot: new CharacterStateSnapshot(energy: 70, hunger: 85, socialNeed: 20, stress: 20)
        );

        var result = await _decisionService.DecideNextActionAsync(request);

        Assert.Equal(AutonomousDecisionAction.PerformActivity, result.Action);
        Assert.NotNull(result.Candidate);
        Assert.Contains(result.Candidate.ActivityType, new[] { CharacterActivityType.Eating, CharacterActivityType.Cooking });
    }

    [Fact]
    public async Task StateNeeds_HighSocialNeed_TriggersSocializing()
    {
        var charId = Guid.NewGuid();
        // High social need character (SocialNeed = 90) at 13:00 (Midday)
        var request = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: new DateTime(2026, 8, 28, 13, 0, 0, DateTimeKind.Utc),
            CurrentLocation: "Town Square",
            TimeBucket: "2026-08-28T13:00",
            StateSnapshot: new CharacterStateSnapshot(energy: 80, hunger: 10, socialNeed: 90, stress: 20)
        );

        var result = await _decisionService.DecideNextActionAsync(request);

        Assert.Equal(AutonomousDecisionAction.PerformActivity, result.Action);
        Assert.NotNull(result.Candidate);
        Assert.Equal(CharacterActivityType.Socializing, result.Candidate.ActivityType);
    }

    [Fact]
    public async Task IncompatibleState_Exhausted_FiltersOutExercising()
    {
        var charId = Guid.NewGuid();
        // Morning routine: Adventurer usually exercises, but energy is 10 (exhausted)
        var request = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: new DateTime(2026, 8, 28, 7, 0, 0, DateTimeKind.Utc),
            CurrentLocation: "Training Yard",
            TimeBucket: "2026-08-28T07:00",
            PersonalityPrompt: "Warrior Adventurer",
            StateSnapshot: new CharacterStateSnapshot(energy: 10, hunger: 40, socialNeed: 20, stress: 20)
        );

        var result = await _decisionService.DecideNextActionAsync(request);

        Assert.Equal(AutonomousDecisionAction.PerformActivity, result.Action);
        Assert.NotNull(result.Candidate);
        Assert.NotEqual(CharacterActivityType.Exercising, result.Candidate.ActivityType);
    }

    [Fact]
    public async Task DeterministicTieBreaking_ProducesIdenticalOutcomeForSameSeed()
    {
        var charId = Guid.NewGuid();
        var time = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        var timeBucket = "2026-08-28T10:00";

        var request1 = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: time,
            CurrentLocation: "Grand Library",
            TimeBucket: timeBucket,
            PersonalityPrompt: "Scholar",
            SceneRevision: 3
        );

        var request2 = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: time,
            CurrentLocation: "Grand Library",
            TimeBucket: timeBucket,
            PersonalityPrompt: "Scholar",
            SceneRevision: 3
        );

        var res1 = await _decisionService.DecideNextActionAsync(request1);
        var res2 = await _decisionService.DecideNextActionAsync(request2);

        Assert.Equal(res1.Candidate!.ActivityType, res2.Candidate!.ActivityType);
        Assert.Equal(res1.Candidate.DecisionFingerprint, res2.Candidate.DecisionFingerprint);
        Assert.Equal(res1.Candidate.Reason, res2.Candidate.Reason);
    }
}
