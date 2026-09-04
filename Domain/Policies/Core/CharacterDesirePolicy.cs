using System;
using System.Collections.Generic;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that derives CharacterDesires and CharacterMotivations
/// from CharacterInternalExperience, modulated by CharacterAppraisal and CharacterEmotion.
/// Zero side-effects, zero LLM, zero DB, zero random, zero clock.
/// </summary>
public sealed class CharacterDesirePolicy : ICharacterDesirePolicy
{
    public CharacterDesireEvaluation Evaluate(
        CharacterInternalExperience experience,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion,
        CharacterBlueprint? blueprint = null)
    {
        ArgumentNullException.ThrowIfNull(experience, nameof(experience));
        ArgumentNullException.ThrowIfNull(appraisal, nameof(appraisal));
        ArgumentNullException.ThrowIfNull(emotion, nameof(emotion));

        var desires = new List<CharacterDesire>(7);

        // 1. Derive Base Desires with Emotion Amplification / Suppression
        // Rules:
        // - Base desire is grounded in subjective internal experience (PR39).
        // - Emotion (PR40) acts as an amplifier or suppressor.
        // - Emotion NEVER creates a desire if the underlying base need is 0.
        desires.Add(CreateFoodDesire(experience, appraisal, emotion));
        desires.Add(CreateRestDesire(experience, appraisal, emotion));
        desires.Add(CreateStressReliefDesire(experience, appraisal, emotion));
        desires.Add(CreateSocialDesire(experience, appraisal, emotion));
        desires.Add(CreateComfortDesire(experience, appraisal, emotion));
        desires.Add(CreateSafetyDesire(experience, appraisal, emotion));
        desires.Add(CreateRecoveryDesire(experience, appraisal, emotion));

        // 2. Select Dominant Desire: Highest Intensity first, with strict deterministic tie-breaking:
        // Precedence: NeedSafety > NeedFood > NeedRest > NeedReduceStress > NeedSocialConnection > NeedComfort > NeedPhysicalRecovery
        var dominantDesire = SelectDominantDesire(desires);

        return new CharacterDesireEvaluation(
            characterId: experience.CharacterId,
            stateVersion: experience.StateVersion,
            desires: desires,
            dominantDesire: dominantDesire
        );
    }

    private static CharacterDesire CreateFoodDesire(
        CharacterInternalExperience exp,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion)
    {
        double baseNeed = exp.Hunger.Intensity.Value;
        double intensity = ApplyEmotionModulation(baseNeed, DesireSource.Hunger, appraisal, emotion);

        var motivation = new CharacterMotivation(MotivationType.HungerDriven, intensity, DesireSource.Hunger);
        return new CharacterDesire(DesireType.NeedFood, intensity, DesireSource.Hunger, motivation);
    }

    private static CharacterDesire CreateRestDesire(
        CharacterInternalExperience exp,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion)
    {
        double baseNeed = exp.Energy.Intensity.Value; // Deficit in energy -> Fatigue intensity
        double intensity = ApplyEmotionModulation(baseNeed, DesireSource.Energy, appraisal, emotion);

        var motivation = new CharacterMotivation(MotivationType.RestorationDriven, intensity, DesireSource.Energy);
        return new CharacterDesire(DesireType.NeedRest, intensity, DesireSource.Energy, motivation);
    }

    private static CharacterDesire CreateStressReliefDesire(
        CharacterInternalExperience exp,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion)
    {
        double baseNeed = exp.Stress.Intensity.Value;
        double intensity = ApplyEmotionModulation(baseNeed, DesireSource.Stress, appraisal, emotion);

        var motivation = new CharacterMotivation(MotivationType.StressReliefDriven, intensity, DesireSource.Stress);
        return new CharacterDesire(DesireType.NeedReduceStress, intensity, DesireSource.Stress, motivation);
    }

    private static CharacterDesire CreateSocialDesire(
        CharacterInternalExperience exp,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion)
    {
        double baseNeed = exp.SocialNeed.Intensity.Value;
        double intensity = ApplyEmotionModulation(baseNeed, DesireSource.SocialNeed, appraisal, emotion);

        var motivation = new CharacterMotivation(MotivationType.ConnectionDriven, intensity, DesireSource.SocialNeed);
        return new CharacterDesire(DesireType.NeedSocialConnection, intensity, DesireSource.SocialNeed, motivation);
    }

    private static CharacterDesire CreateComfortDesire(
        CharacterInternalExperience exp,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion)
    {
        double baseNeed = exp.Comfort.Intensity.Value; // Deficit in comfort -> Discomfort intensity
        double intensity = ApplyEmotionModulation(baseNeed, DesireSource.Comfort, appraisal, emotion);

        var motivation = new CharacterMotivation(MotivationType.ComfortDriven, intensity, DesireSource.Comfort);
        return new CharacterDesire(DesireType.NeedComfort, intensity, DesireSource.Comfort, motivation);
    }

    private static CharacterDesire CreateSafetyDesire(
        CharacterInternalExperience exp,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion)
    {
        // Safety desire only emerges when supported by high stress/anxiety pressure
        double baseNeed = exp.Stress.Level >= StressLevel.Stressed
            ? Math.Max(0.0, exp.Stress.Intensity.Value - 0.20)
            : 0.0;

        double intensity = ApplyEmotionModulation(baseNeed, DesireSource.Stress, appraisal, emotion);

        var motivation = new CharacterMotivation(MotivationType.SafetyDriven, intensity, DesireSource.Stress);
        return new CharacterDesire(DesireType.NeedSafety, intensity, DesireSource.Stress, motivation);
    }

    private static CharacterDesire CreateRecoveryDesire(
        CharacterInternalExperience exp,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion)
    {
        // Physical recovery emerges from high physical depletion (hunger or exhaustion)
        double baseNeed = Math.Max(exp.Hunger.Intensity.Value, exp.Energy.Intensity.Value) > 0.70
            ? Math.Min(exp.Hunger.Intensity.Value, exp.Energy.Intensity.Value)
            : 0.0;

        double intensity = ApplyEmotionModulation(baseNeed, DesireSource.Energy, appraisal, emotion);

        var motivation = new CharacterMotivation(MotivationType.RecoveryDriven, intensity, DesireSource.Energy);
        return new CharacterDesire(DesireType.NeedPhysicalRecovery, intensity, DesireSource.Energy, motivation);
    }

    private static double ApplyEmotionModulation(
        double baseNeed,
        DesireSource source,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion)
    {
        // Invariant: Emotion CANNOT create a desire if the underlying need is 0.0
        if (baseNeed <= 0.0)
        {
            return 0.0;
        }

        double modulated = baseNeed;

        // 1. Amplification Rules
        if (emotion.Type == EmotionType.Frustration && MatchesSource(appraisal.Source, source))
        {
            modulated += 0.15 * emotion.Intensity;
        }
        else if (emotion.Type == EmotionType.Loneliness && source == DesireSource.SocialNeed)
        {
            modulated += 0.15 * emotion.Intensity;
        }
        else if (emotion.Type == EmotionType.Fatigue && source == DesireSource.Energy)
        {
            modulated += 0.15 * emotion.Intensity;
        }
        else if ((emotion.Type == EmotionType.Anxiety || emotion.Type == EmotionType.Stress) && source == DesireSource.Stress)
        {
            modulated += 0.15 * emotion.Intensity;
        }
        else if (emotion.Type == EmotionType.Discomfort && source == DesireSource.Comfort)
        {
            modulated += 0.15 * emotion.Intensity;
        }
        // 2. Suppression Rules
        else if (emotion.Type == EmotionType.Relief)
        {
            modulated -= 0.15 * emotion.Intensity;
        }

        return Math.Clamp(modulated, 0.0, 1.0);
    }

    private static bool MatchesSource(AppraisalSource appraisalSource, DesireSource desireSource) =>
        (appraisalSource, desireSource) switch
        {
            (AppraisalSource.Hunger, DesireSource.Hunger) => true,
            (AppraisalSource.Energy, DesireSource.Energy) => true,
            (AppraisalSource.Stress, DesireSource.Stress) => true,
            (AppraisalSource.SocialNeed, DesireSource.SocialNeed) => true,
            (AppraisalSource.Comfort, DesireSource.Comfort) => true,
            (AppraisalSource.Mood, DesireSource.Mood) => true,
            _ => false
        };

    private static CharacterDesire SelectDominantDesire(IReadOnlyList<CharacterDesire> desires)
    {
        // Deterministic Precedence:
        // NeedSafety > NeedFood > NeedRest > NeedReduceStress > NeedSocialConnection > NeedComfort > NeedPhysicalRecovery
        static int DesirePrecedence(DesireType type) => type switch
        {
            DesireType.NeedSafety => 1,
            DesireType.NeedFood => 2,
            DesireType.NeedRest => 3,
            DesireType.NeedReduceStress => 4,
            DesireType.NeedSocialConnection => 5,
            DesireType.NeedComfort => 6,
            DesireType.NeedPhysicalRecovery => 7,
            _ => 99
        };

        CharacterDesire best = desires[0];

        for (int i = 1; i < desires.Count; i++)
        {
            var candidate = desires[i];

            // 1. Highest Intensity wins
            if (candidate.Intensity > best.Intensity)
            {
                best = candidate;
            }
            // 2. Tie-breaking precedence only when intensities are strictly equal
            else if (candidate.Intensity == best.Intensity)
            {
                if (DesirePrecedence(candidate.Type) < DesirePrecedence(best.Type))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }
}
