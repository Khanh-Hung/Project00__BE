using System;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable snapshot of a character's long-term personality traits at the start of a cognitive cycle.
/// Invariant: Once created, the snapshot never mutates during the cycle.
/// Invariant: Converted to effective PsychologyProfile to influence Perception and Emotion policies.
/// </summary>
public sealed record CharacterPersonalitySnapshot(
    Guid CharacterId,
    uint Version,
    int Warmth,
    int Openness,
    int Assertiveness,
    int Conscientiousness,
    int SocialConfidence,
    int TrustDisposition,
    int EmotionalStability,
    DateTimeOffset SnapshotAtUtc
)
{
    public PsychologyProfile ToEffectivePsychology(PsychologyProfile? baseProfile = null)
    {
        var basePsych = baseProfile ?? PsychologyProfile.Default;

        // EmotionalStability modulates StressSensitivity and MoodReactivity
        // Baseline 50: multiplier = 1.0. At 100: multiplier = 0.5. At 0: multiplier = 1.5.
        decimal stabilityMultiplier = 1.5m - ((decimal)EmotionalStability / 100m);

        // SocialConfidence & Warmth modulate SocialSensitivity
        // Baseline (50+50)/200 = 0.5: multiplier = 1.0. At (100+100): 1.5. At (0+0): 0.5.
        decimal socialMultiplier = 0.5m + (((decimal)SocialConfidence + (decimal)Warmth) / 200m);

        // Conscientiousness modulates FatigueSensitivity (higher conscientiousness resists fatigue)
        // Baseline 50: multiplier = 1.0. At 100: multiplier = 0.75. At 0: multiplier = 1.25.
        decimal conscientiousnessMultiplier = 1.25m - (((decimal)Conscientiousness / 100m) * 0.5m);

        return basePsych with
        {
            StressSensitivity = Math.Clamp(basePsych.StressSensitivity * stabilityMultiplier, 0.1m, 3.0m),
            MoodReactivity = Math.Clamp(basePsych.MoodReactivity * stabilityMultiplier, 0.1m, 3.0m),
            SocialSensitivity = Math.Clamp(basePsych.SocialSensitivity * socialMultiplier, 0.1m, 3.0m),
            FatigueSensitivity = Math.Clamp(basePsych.FatigueSensitivity * conscientiousnessMultiplier, 0.1m, 3.0m),
            Personality = this
        };
    }
}
