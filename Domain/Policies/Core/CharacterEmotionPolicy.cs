using System;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that converts CharacterAppraisals into distinct CharacterEmotions.
/// Completely decoupled: receives CharacterAppraisal, returns CharacterEmotion.
/// Zero dependencies, zero side-effects, zero LLM, zero DB, zero random, zero clock.
/// </summary>
public sealed class CharacterEmotionPolicy : ICharacterEmotionPolicy
{
    public CharacterEmotion Evaluate(
        CharacterAppraisal appraisal,
        CharacterBlueprint? blueprint = null)
    {
        ArgumentNullException.ThrowIfNull(appraisal, nameof(appraisal));

        // In PR39, psychological sensitivities (HungerSensitivity, FatigueSensitivity, StressSensitivity,
        // SocialSensitivity, ComfortSensitivity, MoodReactivity) have already been applied to produce
        // the subjective perception intensities. The Appraisal directly carries this subjective intensity.
        // PR40 preserves appraisal.Intensity directly as the emotion intensity without double-modulation
        // or applying MoodReactivity globally across non-mood appraisals.
        double intensity = appraisal.Intensity;

        // Map Appraisal to canonical EmotionType and EmotionalValence
        var (emotionType, valence) = MapAppraisalToEmotion(appraisal, intensity);

        return new CharacterEmotion(
            type: emotionType,
            intensity: intensity,
            valence: valence,
            appraisal: appraisal
        );
    }

    private static (EmotionType Type, EmotionalValence Valence) MapAppraisalToEmotion(
        CharacterAppraisal appraisal,
        double intensity)
    {
        // Neutral or zero intensity default (also cleanly handles neutral baseline markers: Safety, SocialConnection, PhysicalRestoration)
        if (appraisal.Polarity == AppraisalPolarity.Neutral || intensity == 0.0)
        {
            return (EmotionType.Neutral, EmotionalValence.Neutral);
        }

        return appraisal.Type switch
        {
            // Negative Appraisals -> Negative Emotions
            AppraisalType.PhysicalDeprivation =>
                intensity >= 0.60
                    ? (EmotionType.Frustration, EmotionalValence.Negative)
                    : (EmotionType.Concern, EmotionalValence.Negative),

            AppraisalType.Fatigue =>
                (EmotionType.Fatigue, EmotionalValence.Negative),

            AppraisalType.SocialDeprivation =>
                (EmotionType.Loneliness, EmotionalValence.Negative),

            AppraisalType.StressPressure =>
                intensity >= 0.60
                    ? (EmotionType.Stress, EmotionalValence.Negative)
                    : (EmotionType.Anxiety, EmotionalValence.Negative),

            AppraisalType.Discomfort =>
                (EmotionType.Discomfort, EmotionalValence.Negative),

            AppraisalType.NegativeMood =>
                intensity >= 0.60
                    ? (EmotionType.Sadness, EmotionalValence.Negative)
                    : (EmotionType.Concern, EmotionalValence.Negative),

            // Positive Appraisals -> Positive Emotions
            AppraisalType.Recovery =>
                intensity >= 0.70
                    ? (EmotionType.Joy, EmotionalValence.Positive)
                    : (EmotionType.Relief, EmotionalValence.Positive),

            AppraisalType.Comfort =>
                (EmotionType.Content, EmotionalValence.Positive),

            AppraisalType.PositiveMood =>
                intensity >= 0.60
                    ? (EmotionType.Joy, EmotionalValence.Positive)
                    : (EmotionType.Content, EmotionalValence.Positive),

            _ => (EmotionType.Neutral, EmotionalValence.Neutral)
        };
    }
}
