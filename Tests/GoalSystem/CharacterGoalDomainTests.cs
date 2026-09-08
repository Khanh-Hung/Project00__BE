using System;
using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.GoalSystem;

public sealed class CharacterGoalDomainTests
{
    [Fact]
    public void Goal_CannotHaveNegativeProgress()
    {
        var charId = Guid.NewGuid();

        // Constructor validation
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CharacterGoal(charId, "BuildRelationship", initialProgress: -10));

        // UpdateProgress validation
        var goal = new CharacterGoal(charId, "BuildRelationship", initialProgress: 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => goal.UpdateProgress(-5, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => goal.RecordProgress(-1, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Goal_CannotHaveProgressAbove100()
    {
        var charId = Guid.NewGuid();

        // Constructor validation
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CharacterGoal(charId, "BuildRelationship", initialProgress: 101));

        // UpdateProgress validation
        var goal = new CharacterGoal(charId, "BuildRelationship", initialProgress: 50);
        Assert.Throws<ArgumentOutOfRangeException>(() => goal.UpdateProgress(101, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Goal_DraftCanBecomeActive()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(
            charId,
            "BuildRelationship",
            initialStatus: CharacterGoalStatus.Draft);

        Assert.Equal(CharacterGoalStatus.Draft, goal.Status);

        goal.Activate(DateTimeOffset.UtcNow);

        Assert.Equal(CharacterGoalStatus.Active, goal.Status);
        Assert.NotNull(goal.StartedAt);
    }

    [Fact]
    public void Goal_ActiveCanBecomeCompleted()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(
            charId,
            "BuildRelationship",
            initialStatus: CharacterGoalStatus.Active);

        goal.Complete(DateTimeOffset.UtcNow);

        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.NotNull(goal.CompletedAt);
        Assert.Equal(1.0f, goal.Progress);
        Assert.Equal(100, goal.ProgressPercentage);
    }

    [Fact]
    public void Goal_ActiveCanBeCancelled()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(
            charId,
            "BuildRelationship",
            initialStatus: CharacterGoalStatus.Active);

        goal.Cancel(DateTimeOffset.UtcNow);

        Assert.Equal(CharacterGoalStatus.Cancelled, goal.Status);
        Assert.NotNull(goal.CancelledAt);
    }

    [Fact]
    public void Goal_CompletedCannotBecomeActive()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "BuildRelationship");
        goal.Complete(DateTimeOffset.UtcNow);

        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.Throws<InvalidOperationException>(() => goal.Activate(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Goal_CompletedCannotBecomeCancelled()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "BuildRelationship");
        goal.Complete(DateTimeOffset.UtcNow);

        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.Throws<InvalidOperationException>(() => goal.Cancel(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Goal_CancelledCannotBecomeActive()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "BuildRelationship");
        goal.Cancel(DateTimeOffset.UtcNow);

        Assert.Equal(CharacterGoalStatus.Cancelled, goal.Status);
        Assert.Throws<InvalidOperationException>(() => goal.Activate(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Goal_Progress100_CompletesGoal()
    {
        var charId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var goal = new CharacterGoal(charId, "BuildRelationship", initialProgress: 50);

        goal.UpdateProgress(100, now);

        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.Equal(1.0f, goal.Progress);
        Assert.Equal(100, goal.ProgressPercentage);
        Assert.NotNull(goal.CompletedAt);
    }

    [Fact]
    public void LegacyGoalProgress_RecordContribution_UsesTargetValueSemantics()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "GrandProject", CharacterGoalType.PersonalGrowth, targetValue: 1000);

        goal.RecordProgress(50, DateTimeOffset.UtcNow);

        Assert.Equal(50, goal.CurrentValue);
        Assert.Equal(0.05f, goal.Progress);
        Assert.Equal(5, goal.ProgressPercentage);
    }

    [Fact]
    public void LegacyGoalProgress_DoesNotTreatContributionAsPercentage()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "LargeTask", CharacterGoalType.PersonalGrowth, targetValue: 500);

        goal.RecordProgress(25, DateTimeOffset.UtcNow);

        Assert.Equal(25, goal.CurrentValue);
        Assert.Equal(0.05f, goal.Progress);
        Assert.NotEqual(0.25f, goal.Progress);
        Assert.NotEqual(25f, goal.Progress);
    }

    [Fact]
    public void LegacyGoalProgress_MilestoneContributionPropagationIsPreserved()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "MultiStage", CharacterGoalType.PersonalGrowth, targetValue: 100);
        var m1 = goal.AddMilestone("Step 1", 1, 40);
        var m2 = goal.AddMilestone("Step 2", 2, 60);

        Assert.Equal(CharacterGoalMilestoneStatus.Active, m1.Status);
        Assert.Equal(CharacterGoalMilestoneStatus.Pending, m2.Status);

        goal.RecordProgress(50, DateTimeOffset.UtcNow);

        // m1 (target 40) is complete, remaining 10 overflowed into m2
        Assert.Equal(CharacterGoalMilestoneStatus.Completed, m1.Status);
        Assert.Equal(40, m1.CurrentValue);
        Assert.Equal(CharacterGoalMilestoneStatus.Active, m2.Status);
        Assert.Equal(10, m2.CurrentValue);
        Assert.Equal(50, goal.CurrentValue);
        Assert.Equal(0.5f, goal.Progress);
        Assert.Equal(50, goal.ProgressPercentage);
    }

    [Fact]
    public void LegacyGoalProgress_CompletesGoalAtTargetValue()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "TargetGoal", CharacterGoalType.PersonalGrowth, targetValue: 80);

        goal.RecordProgress(85, DateTimeOffset.UtcNow);

        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.Equal(85, goal.CurrentValue);
        Assert.Equal(1.0f, goal.Progress);
        Assert.Equal(100, goal.ProgressPercentage);
        Assert.NotNull(goal.CompletedAt);
    }
}
