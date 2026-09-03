using System;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that converts CharacterAppraisals into distinct CharacterEmotions.
/// Zero side-effects, zero LLM, zero DB, zero random, zero clock.
/// </summary>
public sealed class CharacterEmotionPolicy : ICharacterEmotionPolicy
{
    private readonly ICharacterAppraisalPolicy _appraisalPolicy;

    public CharacterEmotionPolicy(ICharacterAppraisalPolicy? appraisalPolicy = null)
    {
        _appraisalPolicy = appraisalPolicy ?? new CharacterAppraisalPolicy();
    }

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

    public CharacterEmotion Evaluate(
        CharacterInternalExperience experience,
        CharacterAppraisal appraisal,
        CharacterBlueprint? blueprint = null)
    {
        ArgumentNullException.ThrowIfNull(experience, nameof(experience));
        return Evaluate(appraisal, blueprint);
    }

    public CharacterEmotion EvaluateDominant(
        CharacterInternalExperience experience,
        CharacterBlueprint? blueprint = null)
    {
        ArgumentNullException.ThrowIfNull(experience, nameof(experience));

        var dominantAppraisal = _appraisalPolicy.Evaluate(experience, blueprint);
        return Evaluate(dominantAppraisal, blueprint);
    }

    private static (EmotionType Type, EmotionalValence Valence) MapAppraisalToEmotion(
        CharacterAppraisal appraisal,
        double intensity)
    {
        // Neutral or zero intensity default
        if (appraisal.Polarity == AppraisalPolarity.Neutral || intensity == 0.0)
        {
            return (EmotionType.Neutral, EmotionalValence.Neutral);
        }

        return appraisal.Type switch
        {
            AppraisalType.PhysicalDeprivation =>
                intensity >= 0.60
                    ? (EmotionType.Frustration, EmotionalValence.Negative)
                    : (EmotionType.Concern, EmotionalValence.Negative),

            AppraisalType.Fatigue =>
                (EmotionType.Fatigue, EmotionalValence.Negative),

            AppraisalType.SocialDeprivation =>
                (EmotionType.Loneliness, EmotionalValence.Negative),

            AppraisalType.SocialConnection =>
                intensity >= 0.70
                    ? (EmotionType.Joy, EmotionalValence.Positive)
                    : (EmotionType.Content, EmotionalValence.Positive),

            AppraisalType.StressPressure =>
                intensity >= 0.60
                    ? (EmotionType.Stress, EmotionalValence.Negative)
                    : (EmotionType.Anxiety, EmotionalValence.Negative),

            AppraisalType.Safety =>
                (EmotionType.Content, EmotionalValence.Positive),

            AppraisalType.Discomfort =>
                (EmotionType.Discomfort, EmotionalValence.Negative),

            AppraisalType.Comfort =>
                (EmotionType.Content, EmotionalValence.Positive),

            AppraisalType.PhysicalRestoration or AppraisalType.Recovery =>
                intensity >= 0.70
                    ? (EmotionType.Joy, EmotionalValence.Positive)
                    : (EmotionType.Relief, EmotionalValence.Positive),

            AppraisalType.NegativeMood =>
                intensity >= 0.60
                    ? (EmotionType.Sadness, EmotionalValence.Negative)
                    : (EmotionType.Concern, EmotionalValence.Negative),

            AppraisalType.PositiveMood =>
                intensity >= 0.60
                    ? (EmotionType.Joy, EmotionalValence.Positive)
                    : (EmotionType.Content, EmotionalValence.Positive),

            _ => (EmotionType.Neutral, EmotionalValence.Neutral)
        };
    }
}
