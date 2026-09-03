using Application.Contracts.Activities;
using Application.Contracts.Goals;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class GoalActivityRelevanceTests
{
    private readonly CharacterActivityDecisionService _service;

    public GoalActivityRelevanceTests()
    {
        _service = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
    }

    [Fact]
    public void Policy_EvaluatesCulinaryGoal_AsHighRelevanceForCooking()
    {
        var res = GoalActivityRelevancePolicy.Evaluate("Master Royal French Pastries", "Baking delicious pastries", CharacterGoalType.SkillDevelopment, CharacterActivityType.Cooking);

        Assert.True(res.Score >= 0.9f);
        Assert.Contains("culinary", res.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveCulinaryGoal_BiasesDecisionTowardsCookingInEvening()
    {
        var charId = new Guid("11111111-2222-3333-4444-555555555555");
        var eveningTime = new DateTime(2026, 8, 28, 19, 0, 0, DateTimeKind.Utc); // 19:00

        var goal = new CharacterGoalSnapshot(
            GoalId: Guid.NewGuid(),
            CharacterId: charId,
            Title: "Master Artisan Culinary Dishes",
            GoalType: CharacterGoalType.SkillDevelopment,
            Priority: CharacterGoalPriority.High,
            Status: CharacterGoalStatus.Active,
            Progress: 0.1f,
            CurrentValue: 1,
            TargetValue: 10
        );

        var request = new CharacterActivityDecisionRequest(
            CharacterId: charId,
            CurrentTime: eveningTime,
            CurrentLocation: "Kitchen Sanctuary",
            TimeBucket: "2026-08-28T19:00",
            Goals: new[] { goal }
        );

        var candidate = await _service.DecideAsync(request);

        Assert.NotNull(candidate);
        Assert.Equal(CharacterActivityType.Cooking, candidate.ActivityType);
        Assert.NotNull(candidate.GoalId);
        Assert.Equal(goal.GoalId, candidate.GoalId);
        Assert.NotNull(candidate.GoalTitle);
    }
}
