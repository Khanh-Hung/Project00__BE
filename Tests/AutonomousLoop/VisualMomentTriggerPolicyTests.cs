using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Xunit;

namespace Tests.AutonomousLoop;

public sealed class VisualMomentTriggerPolicyTests
{
    [Fact]
    public void HighValueActivities_TriggerVisualMoment()
    {
        var now = DateTime.UtcNow;

        var exp = VisualMomentPolicy.Evaluate(CharacterActivityType.Exploring, "Ancient Ruins", null, now, null);
        var ready = VisualMomentPolicy.Evaluate(CharacterActivityType.GettingReady, "Bedroom", null, now, null);
        var bath = VisualMomentPolicy.Evaluate(CharacterActivityType.Bathing, "Hot Springs", null, now, null);

        Assert.True(exp.ShouldGenerate);
        Assert.True(ready.ShouldGenerate);
        Assert.True(bath.ShouldGenerate);
    }

    [Fact]
    public void MilestoneCompleted_TriggersVisualMoment_RegardlessOfSedentaryActivity()
    {
        var now = DateTime.UtcNow;

        // Reading is normally filtered (ShouldGenerate = false), but milestone achievement forces visual moment!
        var decision = VisualMomentPolicy.Evaluate(
            activityType: CharacterActivityType.Reading,
            activityLocation: "Study",
            currentVisualState: null,
            currentTime: now,
            lastVisualGenerationAt: null,
            isMilestoneCompleted: true
        );

        Assert.True(decision.ShouldGenerate);
        Assert.Equal(ActivityPriority.Critical, decision.Priority);
        Assert.Contains("Milestone achieved", decision.Reason);
    }

    [Fact]
    public void VisualCooldown_SuppressesRapidVisualSpam()
    {
        var now = DateTime.UtcNow;
        var recentGeneration = now.AddMinutes(-30); // 30 minutes ago (< 1 hour default cooldown)

        var decision = VisualMomentPolicy.Evaluate(
            activityType: CharacterActivityType.Exploring,
            activityLocation: "Forest",
            currentVisualState: null,
            currentTime: now,
            lastVisualGenerationAt: recentGeneration
        );

        Assert.False(decision.ShouldGenerate);
        Assert.NotNull(decision.CooldownRemaining);
        Assert.True(decision.CooldownRemaining.Value.TotalMinutes > 0);
    }
}
