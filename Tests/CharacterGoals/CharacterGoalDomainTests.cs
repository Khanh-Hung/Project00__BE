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
        var goal = new CharacterGoal(charId, "Master Arcane Alchemy", CharacterGoalType.SkillDevelopment, 100, DateTimeOffset.UtcNow, CharacterGoalPriority.High, "Master the 7 stages of alchemy");

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
        var goal = new CharacterGoal(Guid.NewGuid(), "Explore Northern Lands", CharacterGoalType.Exploration, 50, DateTimeOffset.UtcNow);

        // Pause
        goal.Pause(DateTimeOffset.UtcNow);
        Assert.Equal(CharacterGoalStatus.Paused, goal.Status);
        Assert.NotNull(goal.PausedAt);

        // Resume
        goal.Resume(DateTimeOffset.UtcNow);
        Assert.Equal(CharacterGoalStatus.Active, goal.Status);
        Assert.Null(goal.PausedAt);

        // Complete
        goal.Complete(DateTimeOffset.UtcNow);
        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.NotNull(goal.CompletedAt);
        Assert.Equal(1.0f, goal.Progress);
    }

    [Fact]
    public void GoalLifecycle_TerminalStates_CannotTransitionBackToActive()
    {
        // 1. Completed cannot become Active, Paused, Cancelled, or Expired
        var completedGoal = new CharacterGoal(Guid.NewGuid(), "Completed", CharacterGoalType.Lifestyle, 10, DateTimeOffset.UtcNow);
        completedGoal.Complete(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => completedGoal.Activate(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => completedGoal.Pause(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => completedGoal.Cancel(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => completedGoal.Expire(DateTimeOffset.UtcNow));

        // 2. Cancelled cannot become Active, Paused, or Completed
        var cancelledGoal = new CharacterGoal(Guid.NewGuid(), "Cancelled", CharacterGoalType.Relationship, 10, DateTimeOffset.UtcNow);
        cancelledGoal.Cancel(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => cancelledGoal.Activate(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => cancelledGoal.Pause(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => cancelledGoal.Complete(DateTimeOffset.UtcNow));

        // 3. Expired cannot become Active, Paused, or Completed
        var expiredGoal = new CharacterGoal(Guid.NewGuid(), "Expired", CharacterGoalType.Career, 10, DateTimeOffset.UtcNow);
        expiredGoal.Expire(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => expiredGoal.Activate(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => expiredGoal.Pause(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => expiredGoal.Complete(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PausedGoal_CannotCompleteDirectly_MustResumeFirst()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Paused Goal", CharacterGoalType.Career, 10, DateTimeOffset.UtcNow);
        goal.Pause(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => goal.Complete(DateTimeOffset.UtcNow));

        goal.Resume(DateTimeOffset.UtcNow);
        goal.Complete(DateTimeOffset.UtcNow);
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
            new CharacterGoal(Guid.NewGuid(), "Invalid", CharacterGoalType.Custom, invalidTarget, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GoalProgress_InvalidIncrementValue_ThrowsArgumentOutOfRangeException(double invalidIncrement)
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Valid", CharacterGoalType.Custom, 10, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentOutOfRangeException>(() => goal.RecordProgress(invalidIncrement, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AddMilestone_ExceedingGoalTargetValue_ThrowsInvalidOperationException()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Mastery", CharacterGoalType.SkillDevelopment, 100, DateTimeOffset.UtcNow);
        goal.AddMilestone("Step 1", 1, 60);

        // Step 2 with 50 exceeds remaining 40 (60 + 50 = 110 > 100)
        Assert.Throws<InvalidOperationException>(() => goal.AddMilestone("Step 2", 2, 50));
    }
}
