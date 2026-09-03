using System;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that converts authoritative CharacterStateSnapshot and CharacterPsychologyProfile
/// into an immutable CharacterInternalExperience without state mutation or side effects.
/// Distinct and decoupled from World Event Perception.
/// </summary>
public sealed class CharacterInternalExperiencePolicy : ICharacterInternalExperiencePolicy
{
    public CharacterInternalExperience Evaluate(
        CharacterStateSnapshot state,
        CharacterPerceptionContext context,
        PsychologyProfile? psychology = null)
    {
        ArgumentNullException.ThrowIfNull(state, nameof(state));
        ArgumentNullException.ThrowIfNull(context, nameof(context));

        if (context.CharacterId == Guid.Empty)
        {
            throw new ArgumentException("Authoritative non-empty CharacterId is required in CharacterPerceptionContext.", nameof(context));
        }

        // 1. Validate numeric state metrics
        ValidateMetric(state.Hunger, nameof(state.Hunger));
        ValidateMetric(state.Energy, nameof(state.Energy));
        ValidateMetric(state.MoodScalar, nameof(state.MoodScalar));
        ValidateMetric(state.Stress, nameof(state.Stress));
        ValidateMetric(state.SocialNeed, nameof(state.SocialNeed));
        ValidateMetric(state.Comfort, nameof(state.Comfort));

        var psych = psychology ?? PsychologyProfile.Default;
        ValidateSensitivity(psych.HungerSensitivity, nameof(psych.HungerSensitivity));
        ValidateSensitivity(psych.FatigueSensitivity, nameof(psych.FatigueSensitivity));
        ValidateSensitivity(psych.StressSensitivity, nameof(psych.StressSensitivity));
        ValidateSensitivity(psych.SocialSensitivity, nameof(psych.SocialSensitivity));
        ValidateSensitivity(psych.ComfortSensitivity, nameof(psych.ComfortSensitivity));
        ValidateSensitivity(psych.MoodReactivity, nameof(psych.MoodReactivity));

        // 2. Discretize Levels
        var hungerLevel = DiscretizeHunger(state.Hunger);
        var energyLevel = DiscretizeEnergy(state.Energy);
        var stressLevel = DiscretizeStress(state.Stress);
        var socialNeedLevel = DiscretizeSocialNeed(state.SocialNeed);
        var comfortLevel = DiscretizeComfort(state.Comfort);
        var moodLevel = DiscretizeMood(state.MoodScalar);

        // 3. Calculate Subjective Intensities with Correct Semantic Alignment:
        // - Hunger intensity: Hunger / 100 * HungerSensitivity
        // - Fatigue intensity: (100 - Energy) / 100 * FatigueSensitivity (Low Energy -> High Fatigue intensity)
        // - Stress intensity: Stress / 100 * StressSensitivity
        // - SocialNeed intensity: SocialNeed / 100 * SocialSensitivity
        // - Discomfort intensity: (100 - Comfort) / 100 * ComfortSensitivity (Low Comfort -> High Discomfort intensity)
        // - Mood intensity: MoodScalar / 100 * MoodReactivity
        var hungerIntensity = CalculateDirectIntensity(state.Hunger, psych.HungerSensitivity);
        var fatigueIntensity = CalculateDeficitIntensity(state.Energy, psych.FatigueSensitivity);
        var stressIntensity = CalculateDirectIntensity(state.Stress, psych.StressSensitivity);
        var socialNeedIntensity = CalculateDirectIntensity(state.SocialNeed, psych.SocialSensitivity);
        var discomfortIntensity = CalculateDeficitIntensity(state.Comfort, psych.ComfortSensitivity);
        var moodIntensity = CalculateDirectIntensity(state.MoodScalar, psych.MoodReactivity);

        var hungerPerception = new HungerPerception(hungerLevel, hungerIntensity, state.Hunger);
        var energyPerception = new EnergyPerception(energyLevel, fatigueIntensity, state.Energy);
        var stressPerception = new StressPerception(stressLevel, stressIntensity, state.Stress);
        var socialNeedPerception = new SocialNeedPerception(socialNeedLevel, socialNeedIntensity, state.SocialNeed);
        var comfortPerception = new ComfortPerception(comfortLevel, discomfortIntensity, state.Comfort);
        var moodPerception = new MoodPerception(moodLevel, moodIntensity, state.MoodScalar, state.Mood);

        // 4. Calculate Dominant Need with Deterministic Tie-Breaking Precedence:
        // Precedence: Hunger > Energy > SocialNeed > Comfort > Stress
        var dominantNeed = DetermineDominantNeed(state, psych);

        return new CharacterInternalExperience(
            CharacterId: context.CharacterId,
            StateVersion: state.Version,
            EvaluatedAtUtc: context.EvaluatedAtUtc,
            Hunger: hungerPerception,
            Energy: energyPerception,
            Mood: moodPerception,
            Stress: stressPerception,
            SocialNeed: socialNeedPerception,
            Comfort: comfortPerception,
            DominantNeed: dominantNeed
        );
    }

    private static void ValidateMetric(decimal value, string paramName)
    {
        if (value < 0.00m || value > 100.00m)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"State metric must be bounded in [0.00, 100.00]. Actual: {value}");
        }
    }

    private static void ValidateSensitivity(decimal value, string paramName)
    {
        if (value < 0.00m)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Psychology sensitivity trait cannot be negative. Actual: {value}");
        }
    }

    public static HungerLevel DiscretizeHunger(decimal hunger) => hunger switch
    {
        <= 20.00m => HungerLevel.Satisfied,
        <= 40.00m => HungerLevel.SlightlyHungry,
        <= 60.00m => HungerLevel.Hungry,
        <= 80.00m => HungerLevel.VeryHungry,
        _ => HungerLevel.Starving
    };

    public static EnergyLevel DiscretizeEnergy(decimal energy) => energy switch
    {
        <= 20.00m => EnergyLevel.Exhausted,
        <= 40.00m => EnergyLevel.Tired,
        <= 60.00m => EnergyLevel.Moderate,
        <= 80.00m => EnergyLevel.Energized,
        _ => EnergyLevel.HighlyEnergized
    };

    public static StressLevel DiscretizeStress(decimal stress) => stress switch
    {
        <= 20.00m => StressLevel.Calm,
        <= 40.00m => StressLevel.MildPressure,
        <= 60.00m => StressLevel.Stressed,
        <= 80.00m => StressLevel.HighlyStressed,
        _ => StressLevel.Overwhelmed
    };

    public static SocialNeedLevel DiscretizeSocialNeed(decimal socialNeed) => socialNeed switch
    {
        <= 20.00m => SocialNeedLevel.SociallySatisfied,
        <= 40.00m => SocialNeedLevel.MildSocialNeed,
        <= 60.00m => SocialNeedLevel.WantsCompany,
        <= 80.00m => SocialNeedLevel.StrongNeedForCompany,
        _ => SocialNeedLevel.CravesConnection
    };

    public static ComfortLevel DiscretizeComfort(decimal comfort) => comfort switch
    {
        <= 20.00m => ComfortLevel.VeryUncomfortable,
        <= 40.00m => ComfortLevel.Uncomfortable,
        <= 60.00m => ComfortLevel.Neutral,
        <= 80.00m => ComfortLevel.Comfortable,
        _ => ComfortLevel.VeryComfortable
    };

    public static MoodPerceptionLevel DiscretizeMood(decimal mood) => mood switch
    {
        <= 20.00m => MoodPerceptionLevel.Depressed,
        <= 40.00m => MoodPerceptionLevel.Low,
        <= 60.00m => MoodPerceptionLevel.Neutral,
        <= 80.00m => MoodPerceptionLevel.Good,
        _ => MoodPerceptionLevel.Elated
    };

    private static PerceptionIntensity CalculateDirectIntensity(decimal rawMetric, decimal sensitivity)
    {
        decimal normalized = rawMetric / 100.00m;
        decimal scaled = normalized * sensitivity;
        double clamped = Math.Clamp((double)scaled, 0.0, 1.0);
        return new PerceptionIntensity(clamped);
    }

    private static PerceptionIntensity CalculateDeficitIntensity(decimal rawMetric, decimal sensitivity)
    {
        decimal deficitNormalized = (100.00m - rawMetric) / 100.00m;
        decimal scaled = deficitNormalized * sensitivity;
        double clamped = Math.Clamp((double)scaled, 0.0, 1.0);
        return new PerceptionIntensity(clamped);
    }

    private static DominantNeed DetermineDominantNeed(CharacterStateSnapshot state, PsychologyProfile psych)
    {
        decimal hungerPressure = ((decimal)state.Hunger / 100.00m) * psych.HungerSensitivity;
        decimal fatiguePressure = ((100.00m - (decimal)state.Energy) / 100.00m) * psych.FatigueSensitivity;
        decimal socialPressure = ((decimal)state.SocialNeed / 100.00m) * psych.SocialSensitivity;
        decimal discomfortPressure = ((100.00m - (decimal)state.Comfort) / 100.00m) * psych.ComfortSensitivity;
        decimal stressPressure = ((decimal)state.Stress / 100.00m) * psych.StressSensitivity;

        const decimal BaselineThreshold = 0.20m;

        decimal maxPressure = Math.Max(
            hungerPressure,
            Math.Max(
                fatiguePressure,
                Math.Max(socialPressure, Math.Max(discomfortPressure, stressPressure))
            )
        );

        if (maxPressure <= BaselineThreshold)
        {
            return DominantNeed.None;
        }

        // Strict deterministic precedence: Hunger > Energy > SocialNeed > Comfort > Stress
        if (hungerPressure == maxPressure) return DominantNeed.Hunger;
        if (fatiguePressure == maxPressure) return DominantNeed.Energy;
        if (socialPressure == maxPressure) return DominantNeed.SocialNeed;
        if (discomfortPressure == maxPressure) return DominantNeed.Comfort;
        if (stressPressure == maxPressure) return DominantNeed.Stress;

        return DominantNeed.None;
    }
}
