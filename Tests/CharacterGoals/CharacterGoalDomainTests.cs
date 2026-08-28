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
    public void GoalLifecycle_TerminalStates_CannotTransitionBackToActive()
    {
        // 1. Completed cannot become Active, Paused, Cancelled, or Expired
        var completedGoal = new CharacterGoal(Guid.NewGuid(), "Completed", CharacterGoalType.Lifestyle, 10);
        completedGoal.Complete();

        Assert.Throws<InvalidOperationException>(() => completedGoal.Activate());
        Assert.Throws<InvalidOperationException>(() => completedGoal.Pause());
        Assert.Throws<InvalidOperationException>(() => completedGoal.Cancel());
        Assert.Throws<InvalidOperationException>(() => completedGoal.Expire());

        // 2. Cancelled cannot become Active, Paused, or Completed
        var cancelledGoal = new CharacterGoal(Guid.NewGuid(), "Cancelled", CharacterGoalType.Relationship, 10);
        cancelledGoal.Cancel();

        Assert.Throws<InvalidOperationException>(() => cancelledGoal.Activate());
        Assert.Throws<InvalidOperationException>(() => cancelledGoal.Pause());
        Assert.Throws<InvalidOperationException>(() => cancelledGoal.Complete());

        // 3. Expired cannot become Active, Paused, or Completed
        var expiredGoal = new CharacterGoal(Guid.NewGuid(), "Expired", CharacterGoalType.Career, 10);
        expiredGoal.Expire();

        Assert.Throws<InvalidOperationException>(() => expiredGoal.Activate());
        Assert.Throws<InvalidOperationException>(() => expiredGoal.Pause());
        Assert.Throws<InvalidOperationException>(() => expiredGoal.Complete());
    }

    [Fact]
    public void PausedGoal_CannotCompleteDirectly_MustResumeFirst()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Paused Goal", CharacterGoalType.Career, 10);
        goal.Pause();

        Assert.Throws<InvalidOperationException>(() => goal.Complete());

        goal.Resume();
        goal.Complete();
        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GoalCreation_InvalidTargetValue_ThrowsArgumentOutOfRangeException(double invalidTarget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CharacterGoal(Guid.NewGuid(), "Invalid", CharacterGoalType.Custom, invalidTarget));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GoalProgress_InvalidIncrementValue_ThrowsArgumentOutOfRangeException(double invalidIncrement)
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Valid", CharacterGoalType.Custom, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => goal.RecordProgress(invalidIncrement));
    }

    [Fact]
    public void AddMilestone_ExceedingGoalTargetValue_ThrowsInvalidOperationException()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Mastery", CharacterGoalType.SkillDevelopment, 100);
        goal.AddMilestone("Step 1", 1, 60);

        // Step 2 with 50 exceeds remaining 40 (60 + 50 = 110 > 100)
        Assert.Throws<InvalidOperationException>(() => goal.AddMilestone("Step 2", 2, 50));
    }
}
