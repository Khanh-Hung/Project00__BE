using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Xunit;

namespace Tests.CharacterActivities;

public sealed class VisualMomentPolicyTests
{
    [Fact]
    public void MeaningfulActivity_GettingReady_ProducesVisualMoment()
    {
        var now = DateTime.UtcNow;
        var decision = VisualMomentPolicy.Evaluate(
            activityType: CharacterActivityType.GettingReady,
            activityLocation: "Bedchamber",
            currentVisualState: null,
            currentTime: now,
            lastVisualGenerationAt: null
        );

        Assert.True(decision.ShouldGenerate);
        Assert.Equal(ActivityPriority.High, decision.Priority);
    }

    [Fact]
    public void RoutineActivity_Reading_DoesNotProduceVisualMoment()
    {
        var now = DateTime.UtcNow;
        var decision = VisualMomentPolicy.Evaluate(
            activityType: CharacterActivityType.Reading,
            activityLocation: "Library",
            currentVisualState: null,
            currentTime: now,
            lastVisualGenerationAt: null
        );

        Assert.False(decision.ShouldGenerate);
    }

    [Fact]
    public void NewLocation_TriggersVisualMoment_EvenForStandardActivity()
    {
        var charId = Guid.NewGuid();
        var charState = new CharacterVisualState(charId, "Library", 1);
        var now = DateTime.UtcNow;

        var decision = VisualMomentPolicy.Evaluate(
            activityType: CharacterActivityType.Walking,
            activityLocation: "Forgotten Sunken Courtyard", // Location changed!
            currentVisualState: charState,
            currentTime: now,
            lastVisualGenerationAt: null
        );

        Assert.True(decision.ShouldGenerate);
        Assert.Equal(ActivityPriority.High, decision.Priority);
        Assert.Contains("new location", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualSpamCooldown_SuppressesGeneration_WhenTooRecent()
    {
        var now = DateTime.UtcNow;
        var recentGenAt = now.AddMinutes(-15); // Generated 15 min ago (cooldown is 1h)

        var decision = VisualMomentPolicy.Evaluate(
            activityType: CharacterActivityType.GettingReady,
            activityLocation: "Bedchamber",
            currentVisualState: null,
            currentTime: now,
            lastVisualGenerationAt: recentGenAt
        );

        Assert.False(decision.ShouldGenerate);
        Assert.NotNull(decision.CooldownRemaining);
        Assert.Contains("cooldown active", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
