using Domain.Enums;
using Domain.Policies;
using Xunit;

namespace Tests.CharacterActivities;

public sealed class ActivityRepetitionTests
{
    [Fact]
    public void IsRepetitive_SuppressesBackToBackDuplicateActivity()
    {
        var recent = new[] { CharacterActivityType.Reading, CharacterActivityType.Walking };

        // Reading was just performed -> Repetitive
        var isRepetitive = ActivityCooldownPolicy.IsRepetitive(CharacterActivityType.Reading, recent);
        Assert.True(isRepetitive);

        // Cooking was not recently performed -> Not repetitive
        var isNotRepetitive = ActivityCooldownPolicy.IsRepetitive(CharacterActivityType.Cooking, recent);
        Assert.False(isNotRepetitive);
    }

    [Fact]
    public void IsOnCooldown_SuppressesActivityWithinCooldownWindow()
    {
        var now = new DateTime(2026, 8, 28, 12, 15, 0, DateTimeKind.Utc);
        var lastEatingAt = new DateTime(2026, 8, 28, 11, 45, 0, DateTimeKind.Utc); // 30 mins ago

        // Eating has a 2h cooldown -> On cooldown
        var onCooldown = ActivityCooldownPolicy.IsOnCooldown(CharacterActivityType.Eating, lastEatingAt, now);
        Assert.True(onCooldown);

        // 3 hours later -> Cooldown expired
        var later = now.AddHours(3);
        var cooldownExpired = ActivityCooldownPolicy.IsOnCooldown(CharacterActivityType.Eating, lastEatingAt, later);
        Assert.False(cooldownExpired);
    }
}
