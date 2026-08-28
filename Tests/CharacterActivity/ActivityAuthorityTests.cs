using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.CharacterActivities;

public sealed class ActivityAuthorityTests
{
    [Fact]
    public void UserIntent_CancelsAndSupersedes_AutonomousActivity()
    {
        var charId = Guid.NewGuid();
        var autonomousActivity = new CharacterActivity(
            characterId: charId,
            activityType: CharacterActivityType.Reading,
            location: "Library",
            timeBucket: "2026-08-28T14:00",
            decisionFingerprint: "fp_reading",
            source: CharacterActivitySource.Autonomous,
            status: CharacterActivityStatus.Started
        );

        Assert.Equal(CharacterActivityStatus.Started, autonomousActivity.Status);

        // User issues a command: "Come to the kitchen immediately."
        autonomousActivity.Cancel("User commanded immediate relocation to Kitchen.");

        Assert.Equal(CharacterActivityStatus.Cancelled, autonomousActivity.Status);
        Assert.Contains("User commanded", autonomousActivity.Reason);
    }

    [Fact]
    public void ActivityLifecycle_CannotComplete_OnceCancelled()
    {
        var charId = Guid.NewGuid();
        var activity = new CharacterActivity(
            characterId: charId,
            activityType: CharacterActivityType.Walking,
            location: "Courtyard",
            timeBucket: "2026-08-28T15:00",
            decisionFingerprint: "fp_walk",
            source: CharacterActivitySource.Autonomous
        );

        activity.Cancel("Superseded");

        Assert.Throws<InvalidOperationException>(() => activity.Complete());
    }
}
