using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class GoalUserAuthorityTests
{
    [Fact]
    public void ExplicitUserIntent_CancelsAndOverridesGoalDrivenAutonomousActivity()
    {
        var charId = Guid.NewGuid();
        var goalId = Guid.NewGuid();

        // Autonomous goal-driven activity started
        var autonomousActivity = new CharacterActivity(
            characterId: charId,
            activityType: CharacterActivityType.Working,
            location: "Alchemical Lab",
            timeBucket: "2026-08-28T14:00",
            decisionFingerprint: "fp_test_123",
            source: CharacterActivitySource.Autonomous,
            reason: "Researching arcane formulas for goal",
            goalId: goalId,
            status: CharacterActivityStatus.Started
        );

        // User issues immediate command: "Come to the balcony and watch the sunset"
        autonomousActivity.Cancel("User requested character to join them at the balcony.");

        Assert.Equal(CharacterActivityStatus.Cancelled, autonomousActivity.Status);
        Assert.Contains("User requested character", autonomousActivity.Reason);

        // New user-directed activity takes immediate authority
        var userActivity = new CharacterActivity(
            characterId: charId,
            activityType: CharacterActivityType.Relaxing,
            location: "Balcony",
            timeBucket: "2026-08-28T14:00",
            decisionFingerprint: "fp_user_override",
            source: CharacterActivitySource.UserInteraction,
            priority: ActivityPriority.Critical,
            reason: "Direct user command",
            status: CharacterActivityStatus.Started
        );

        Assert.Equal(CharacterActivitySource.UserInteraction, userActivity.Source);
        Assert.Equal(CharacterActivityStatus.Started, userActivity.Status);
    }
}
