using Domain.Enums;

namespace Domain.Policies;

/// <summary>
/// Authoritative policy enforcing activity cooldowns, duration limits, and anti-repetition rules.
/// </summary>
public static class ActivityCooldownPolicy
{
    private static readonly Dictionary<CharacterActivityType, TimeSpan> Cooldowns = new()
    {
        [CharacterActivityType.Idle] = TimeSpan.FromMinutes(15),
        [CharacterActivityType.Reading] = TimeSpan.FromMinutes(30),
        [CharacterActivityType.Eating] = TimeSpan.FromHours(2),
        [CharacterActivityType.Drinking] = TimeSpan.FromMinutes(30),
        [CharacterActivityType.Sleeping] = TimeSpan.FromHours(6),
        [CharacterActivityType.Cooking] = TimeSpan.FromHours(1),
        [CharacterActivityType.Working] = TimeSpan.FromHours(1),
        [CharacterActivityType.Walking] = TimeSpan.FromHours(1),
        [CharacterActivityType.Exercising] = TimeSpan.FromHours(2),
        [CharacterActivityType.Relaxing] = TimeSpan.FromMinutes(30),
        [CharacterActivityType.Bathing] = TimeSpan.FromHours(4),
        [CharacterActivityType.GettingReady] = TimeSpan.FromHours(4),
        [CharacterActivityType.Exploring] = TimeSpan.FromHours(3),
        [CharacterActivityType.Socializing] = TimeSpan.FromHours(2),
        [CharacterActivityType.Custom] = TimeSpan.FromHours(1)
    };

    public static TimeSpan GetCooldown(CharacterActivityType activityType)
    {
        return Cooldowns.TryGetValue(activityType, out var cd) ? cd : TimeSpan.FromHours(1);
    }

    public static bool IsOnCooldown(CharacterActivityType activityType, DateTime? lastPerformedAt, DateTime currentTime)
    {
        if (!lastPerformedAt.HasValue) return false;

        var cooldown = GetCooldown(activityType);
        return (currentTime - lastPerformedAt.Value) < cooldown;
    }

    public static bool IsRepetitive(
        CharacterActivityType candidate,
        IReadOnlyList<CharacterActivityType>? recentActivities,
        int lookbackCount = 2)
    {
        if (recentActivities == null || recentActivities.Count == 0) return false;

        // Never repeat back-to-back unless Idle
        if (recentActivities[0] == candidate && candidate != CharacterActivityType.Idle)
        {
            return true;
        }

        // Count occurrences in recent window
        var occurrences = recentActivities.Take(lookbackCount).Count(a => a == candidate);
        return occurrences >= 2;
    }
}
