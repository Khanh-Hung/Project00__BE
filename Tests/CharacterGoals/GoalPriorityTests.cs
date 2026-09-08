using Application.Contracts.Activities;
using Application.Contracts.Goals;
using Application.Services;
using Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class GoalPriorityTests
{
    private readonly CharacterActivityDecisionService _service;

    public GoalPriorityTests()
    {
        _service = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
    }

    [Fact]
    public async Task CriticalPriorityGoal_OutranksNormalPriorityGoal_ForActivitySelection()
    {
        var charId = Guid.NewGuid();
        var afternoonTime = new DateTime(2026, 8, 28, 15, 0, 0, DateTimeKind.Utc); // 15:00

        var normalGoal = new CharacterGoalSnapshot(
            GoalId: Guid.NewGuid(),
            CharacterId: charId,
            Title: "Casual Reading of Novels",
            GoalType: CharacterGoalType.Lifestyle,
            Priority: CharacterGoalPriority.Low,
            Status: CharacterGoalStatus.Active,
            Progress: 0.2f,
            CurrentValue: 2,
            TargetValue: 10
        );

        var criticalGoal = new CharacterGoalSnapshot(
            GoalId: Guid.NewGuid(),
            CharacterId: charId,
            Title: "Explore Forgotten Tomb Before Sunset",
            GoalType: CharacterGoalType.Exploration,
            Priority: CharacterGoalPriority.Critical,
            Status: CharacterGoalStatus.Active,
            Progress: 0.1f,
            CurrentValue: 1,
            TargetValue: 10
        );

        var request = new CharacterActivityDecisionRequest(
            CharacterId: charId,
            CurrentTime: afternoonTime,
            CurrentLocation: "Sanctuary",
            TimeBucket: "2026-08-28T15:00",
            Goals: new[] { normalGoal, criticalGoal }
        );

        var candidate = await _service.DecideAsync(request);

        Assert.NotNull(candidate);
        Assert.Equal(CharacterActivityType.Exploring, candidate.ActivityType);
        Assert.Equal(criticalGoal.GoalId, candidate.GoalId);
    }
}
