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
        Assert.Throws<ArgumentOutOfRangeException>(() => goal.UpdateProgress(-5));
        Assert.Throws<ArgumentOutOfRangeException>(() => goal.RecordProgress(-1));
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
        Assert.Throws<ArgumentOutOfRangeException>(() => goal.UpdateProgress(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => goal.RecordProgress(60));
    }

    [Fact]
    public void Goal_ScheduledCanBecomeActive()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(
            charId,
            "BuildRelationship",
            initialStatus: CharacterGoalStatus.Scheduled);

        Assert.Equal(CharacterGoalStatus.Scheduled, goal.Status);

        goal.Activate();

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

        goal.Complete();

        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.NotNull(goal.CompletedAt);
        Assert.Equal(100f, goal.Progress);
    }

    [Fact]
    public void Goal_ActiveCanBeCancelled()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(
            charId,
            "BuildRelationship",
            initialStatus: CharacterGoalStatus.Active);

        goal.Cancel();

        Assert.Equal(CharacterGoalStatus.Cancelled, goal.Status);
        Assert.NotNull(goal.CancelledAt);
    }

    [Fact]
    public void Goal_CompletedCannotBecomeActive()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "BuildRelationship");
        goal.Complete();

        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.Throws<InvalidOperationException>(() => goal.Activate());
    }

    [Fact]
    public void Goal_CompletedCannotBecomeCancelled()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "BuildRelationship");
        goal.Complete();

        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.Throws<InvalidOperationException>(() => goal.Cancel());
    }

    [Fact]
    public void Goal_CancelledCannotBecomeActive()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "BuildRelationship");
        goal.Cancel();

        Assert.Equal(CharacterGoalStatus.Cancelled, goal.Status);
        Assert.Throws<InvalidOperationException>(() => goal.Activate());
    }

    [Fact]
    public void Goal_Progress100_CompletesGoal()
    {
        var charId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var goal = new CharacterGoal(charId, "BuildRelationship", initialProgress: 50);

        goal.UpdateProgress(100, now);

        Assert.Equal(CharacterGoalStatus.Completed, goal.Status);
        Assert.Equal(100f, goal.Progress);
        Assert.NotNull(goal.CompletedAt);
    }
}
