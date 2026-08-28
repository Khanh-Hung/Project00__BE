using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class CharacterGoalMilestoneTests
{
    [Fact]
    public void Milestones_AddedToAggregate_FirstMilestoneActivatedByAggregateRoot()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Become Master Wizard", CharacterGoalType.Career, 100);

        var m1 = goal.AddMilestone("Study Cantrips", 1, 20);
        var m2 = goal.AddMilestone("Learn Fireball", 2, 40);
        var m3 = goal.AddMilestone("Master Arcane Teleportation", 3, 40);

        Assert.Equal(3, goal.Milestones.Count);
        Assert.Equal(CharacterGoalMilestoneStatus.Active, m1.Status);
        Assert.Equal(CharacterGoalMilestoneStatus.Pending, m2.Status);
        Assert.Equal(CharacterGoalMilestoneStatus.Pending, m3.Status);
    }

    [Fact]
    public void MilestoneProgression_CascadingOverflow_DistributesContributionAcrossMultipleMilestones()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Master Culinary Arts", CharacterGoalType.SkillDevelopment, 100);
        var m1 = goal.AddMilestone("Knife Skills", 1, 10);
        var m2 = goal.AddMilestone("Baking Basics", 2, 40);
        var m3 = goal.AddMilestone("Host Banquet", 3, 50);

        // Contribution of 25:
        // m1 takes 10, completes.
        // remaining 15 overflows to m2, which activates and has current 15/40.
        goal.RecordProgress(25);

        Assert.Equal(CharacterGoalMilestoneStatus.Completed, m1.Status);
        Assert.Equal(10, m1.CurrentValue);
        Assert.NotNull(m1.CompletedAt);

        Assert.Equal(CharacterGoalMilestoneStatus.Active, m2.Status);
        Assert.Equal(15, m2.CurrentValue);
        Assert.Equal(CharacterGoalMilestoneStatus.Pending, m3.Status);
        Assert.Equal(0.25f, goal.Progress);
        Assert.Equal(25, goal.CurrentValue);

        // Next contribution of 35:
        // m2 needs 25 (40 - 15), takes 25, completes.
        // remaining 10 overflows to m3, which activates and has current 10/50.
        goal.RecordProgress(35);

        Assert.Equal(CharacterGoalMilestoneStatus.Completed, m2.Status);
        Assert.Equal(40, m2.CurrentValue);
        Assert.NotNull(m2.CompletedAt);

        Assert.Equal(CharacterGoalMilestoneStatus.Active, m3.Status);
        Assert.Equal(10, m3.CurrentValue);
        Assert.Equal(0.60f, goal.Progress);
        Assert.Equal(60, goal.CurrentValue);

        // Final contribution of 40:
        // m3 takes 40, completes (50/50).
        // Goal completes (100/100).
        goal.RecordProgress(40);

        Assert.Equal(CharacterGoalMilestoneStatus.Completed, m3.Status);
        Assert.Equal(50, m3.CurrentValue);
        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.Equal(1.0f, goal.Progress);
        Assert.Equal(100, goal.CurrentValue);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MilestoneCreation_InvalidTarget_ThrowsArgumentOutOfRangeException(double invalidTarget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CharacterGoalMilestone(Guid.NewGuid(), "Milestone", 1, invalidTarget));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MilestoneProgress_InvalidAmount_ThrowsArgumentOutOfRangeException(double invalidAmount)
    {
        var m = new CharacterGoalMilestone(Guid.NewGuid(), "Milestone", 1, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => m.RecordProgress(invalidAmount));
    }

    [Fact]
    public void DuplicateMilestoneOrder_ThrowsArgumentException()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Goal", CharacterGoalType.PersonalGrowth, 50);
        goal.AddMilestone("M1", 1, 25);

        Assert.Throws<ArgumentException>(() => goal.AddMilestone("M1 duplicate", 1, 25));
    }
}
