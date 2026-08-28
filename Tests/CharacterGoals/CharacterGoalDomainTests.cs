using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class CharacterGoalDomainTests
{
    [Fact]
    public void GoalCreation_ValidParameters_SetsInitialStateCorrectly()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "Master Arcane Alchemy", CharacterGoalType.SkillDevelopment, 100, CharacterGoalPriority.High, "Master the 7 stages of alchemy");

        Assert.Equal(charId, goal.CharacterId);
        Assert.Equal("Master Arcane Alchemy", goal.Title);
        Assert.Equal(CharacterGoalType.SkillDevelopment, goal.GoalType);
        Assert.Equal(CharacterGoalPriority.High, goal.Priority);
        Assert.Equal(CharacterGoalStatus.Active, goal.Status);
        Assert.Equal(100, goal.TargetValue);
        Assert.Equal(0, goal.CurrentValue);
        Assert.Equal(0f, goal.Progress);
        Assert.NotNull(goal.StartedAt);
        Assert.Null(goal.CompletedAt);
    }

    [Fact]
    public void GoalLifecycle_ValidTransitions_Succeeds()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Explore Northern Lands", CharacterGoalType.Exploration, 50);

        // Pause
        goal.Pause();
        Assert.Equal(CharacterGoalStatus.Paused, goal.Status);
        Assert.NotNull(goal.PausedAt);

        // Resume
        goal.Resume();
        Assert.Equal(CharacterGoalStatus.Active, goal.Status);
        Assert.Null(goal.PausedAt);

        // Complete
        goal.Complete();
        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.NotNull(goal.CompletedAt);
        Assert.Equal(1.0f, goal.Progress);
    }

    [Fact]
    public void GoalLifecycle_InvalidTransitions_ThrowsInvalidOperationException()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Learn Cooking", CharacterGoalType.Lifestyle, 10);
        goal.Complete();

        // Completed cannot become Active
        Assert.Throws<InvalidOperationException>(() => goal.Activate());

        // Completed cannot be Cancelled
        Assert.Throws<InvalidOperationException>(() => goal.Cancel());

        var pausedGoal = new CharacterGoal(Guid.NewGuid(), "Paused Goal", CharacterGoalType.Career, 10);
        pausedGoal.Pause();

        // Paused cannot complete without Resume
        Assert.Throws<InvalidOperationException>(() => pausedGoal.Complete());

        var cancelledGoal = new CharacterGoal(Guid.NewGuid(), "Cancelled Goal", CharacterGoalType.Relationship, 10);
        cancelledGoal.Cancel();

        // Cancelled cannot become Active or Completed
        Assert.Throws<InvalidOperationException>(() => cancelledGoal.Activate());
        Assert.Throws<InvalidOperationException>(() => cancelledGoal.Complete());
    }

    [Fact]
    public void GoalProgress_NegativeTargetOrIncrement_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CharacterGoal(Guid.NewGuid(), "Invalid", CharacterGoalType.Custom, -5));

        var goal = new CharacterGoal(Guid.NewGuid(), "Valid", CharacterGoalType.Custom, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => goal.RecordProgress(-1));
    }
}
