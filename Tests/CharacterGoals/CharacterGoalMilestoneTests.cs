using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class CharacterGoalMilestoneTests
{
    [Fact]
    public void Milestones_AddedInOrder_FirstMilestoneAutomaticallyActive()
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
    public void MilestoneProgression_AdvancesNextMilestone_WhenActiveCompletes()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Master Culinary Arts", CharacterGoalType.SkillDevelopment, 30);
        var m1 = goal.AddMilestone("Knife Skills", 1, 10);
        var m2 = goal.AddMilestone("Baking Basics", 2, 10);
        var m3 = goal.AddMilestone("Host 3-Course Banquet", 3, 10);

        // Progress 10 -> Completes m1 and auto-activates m2
        goal.RecordProgress(10);

        Assert.Equal(CharacterGoalMilestoneStatus.Completed, m1.Status);
        Assert.Equal(10, m1.CurrentValue);
        Assert.NotNull(m1.CompletedAt);

        Assert.Equal(CharacterGoalMilestoneStatus.Active, m2.Status);
        Assert.Equal(CharacterGoalMilestoneStatus.Pending, m3.Status);
        Assert.Equal(CharacterGoalStatus.Active, goal.Status);

        // Progress another 20 -> Completes m2 and m3 and completes goal
        goal.RecordProgress(20);

        Assert.Equal(CharacterGoalMilestoneStatus.Completed, m2.Status);
        Assert.Equal(CharacterGoalMilestoneStatus.Completed, m3.Status);
        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.Equal(1.0f, goal.Progress);
    }

    [Fact]
    public void DuplicateMilestoneOrder_ThrowsArgumentException()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Goal", CharacterGoalType.PersonalGrowth, 50);
        goal.AddMilestone("M1", 1, 25);

        Assert.Throws<ArgumentException>(() => goal.AddMilestone("M1 duplicate", 1, 25));
    }
}
