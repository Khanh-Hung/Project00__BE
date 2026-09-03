using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Centralized deterministic policy defining authoritative state deltas resulting from activity completions.
/// </summary>
public static class CharacterActivityOutcomeStatePolicy
{
    public static CharacterStateDelta CalculateOutcomeDelta(CharacterActivityType activityType)
    {
        var delta = CharacterActivityOutcomePolicy.CalculateDelta(activityType);
        decimal moodDelta = delta.ResultingMood switch
        {
            CharacterMood.Happy => 25m,
            CharacterMood.Excited => 35m,
            CharacterMood.Sad => -25m,
            CharacterMood.Angry => -35m,
            _ => (decimal)delta.MoodIntensityDelta
        };

        return new CharacterStateDelta(
            energyDelta: delta.EnergyDelta,
            hungerDelta: delta.HungerDelta,
            socialNeedDelta: delta.SocialNeedDelta,
            stressDelta: delta.StressDelta,
            comfortDelta: 5m,
            moodDelta: moodDelta
        );
    }
}
