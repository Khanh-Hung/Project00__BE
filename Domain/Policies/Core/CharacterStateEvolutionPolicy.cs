using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic temporal evolution policy for character physiological and psychological needs.
/// Calculates aggregate delta across elapsed time without hourly loops or floating-point drift.
/// </summary>
public sealed class CharacterStateEvolutionPolicy : ICharacterStateEvolutionPolicy
{
    public const decimal HungerRatePerHour = 4.0m;
    public const decimal EnergyRatePerHour = -5.0m;
    public const decimal StressRatePerHour = 1.0m;
    public const decimal SocialNeedRatePerHour = 2.0m;
    public const decimal ComfortRatePerHour = -1.0m;

    public CharacterStateDelta CalculateEvolutionDelta(
        CharacterStateSnapshot currentState,
        DateTime lastEvolvedAtUtc,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(currentState, nameof(currentState));

        if (nowUtc < lastEvolvedAtUtc)
        {
            throw new InvalidOperationException(
                $"Invalid evolution time: target nowUtc ({nowUtc:O}) is earlier than lastEvolvedAtUtc ({lastEvolvedAtUtc:O}).");
        }

        if (nowUtc == lastEvolvedAtUtc)
        {
            return CharacterStateDelta.Zero;
        }

        var totalHours = (decimal)(nowUtc - lastEvolvedAtUtc).TotalHours;
        if (totalHours <= 0m)
        {
            return CharacterStateDelta.Zero;
        }

        var hungerDelta = Math.Round(totalHours * HungerRatePerHour, 2);
        var energyDelta = Math.Round(totalHours * EnergyRatePerHour, 2);
        var stressDelta = Math.Round(totalHours * StressRatePerHour, 2);
        var socialNeedDelta = Math.Round(totalHours * SocialNeedRatePerHour, 2);
        var comfortDelta = Math.Round(totalHours * ComfortRatePerHour, 2);

        // Deterministic mood pressure per hour based on current needs balance
        // Positive: high comfort, high energy, low hunger, low stress
        // Negative: high hunger, low energy, high stress, high social need
        var moodPressurePerHour =
            (currentState.Comfort * 0.05m + currentState.Energy * 0.05m)
            - (currentState.Hunger * 0.06m + currentState.Stress * 0.06m + currentState.SocialNeed * 0.03m);

        // Clamp mood pressure per hour to prevent runaway drift
        var clampedMoodRate = Math.Clamp(moodPressurePerHour, -10.0m, 8.0m);
        var moodDelta = Math.Round(totalHours * clampedMoodRate, 2);

        return new CharacterStateDelta(
            hungerDelta: hungerDelta,
            energyDelta: energyDelta,
            moodDelta: moodDelta,
            stressDelta: stressDelta,
            socialNeedDelta: socialNeedDelta,
            comfortDelta: comfortDelta
        );
    }
}
