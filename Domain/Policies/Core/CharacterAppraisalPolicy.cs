using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that interprets CharacterInternalExperience into subjective CharacterAppraisals.
/// Zero side-effects, zero LLM, zero DB, zero random, zero clock.
/// </summary>
public sealed class CharacterAppraisalPolicy : ICharacterAppraisalPolicy
{
    public CharacterAppraisal Evaluate(
        CharacterInternalExperience experience,
        CharacterBlueprint? blueprint = null)
    {
        ArgumentNullException.ThrowIfNull(experience, nameof(experience));

        var allAppraisals = EvaluateAll(experience, blueprint);

        // Find the appraisal with the highest intensity, breaking ties deterministically:
        // Precedence: Stress > Hunger > SocialNeed > Energy > Comfort > Mood
        return SelectDominantAppraisal(allAppraisals);
    }

    public IReadOnlyList<CharacterAppraisal> EvaluateAll(
        CharacterInternalExperience experience,
        CharacterBlueprint? blueprint = null)
    {
        ArgumentNullException.ThrowIfNull(experience, nameof(experience));

        var list = new List<CharacterAppraisal>(6);

        // 1. Hunger Appraisal
        list.Add(AppraiseHunger(experience.Hunger));

        // 2. Energy Appraisal (PR39 semantic: Intensity is Fatigue)
        list.Add(AppraiseEnergy(experience.Energy));

        // 3. Stress Appraisal
        list.Add(AppraiseStress(experience.Stress));

        // 4. SocialNeed Appraisal
        list.Add(AppraiseSocialNeed(experience.SocialNeed));

        // 5. Comfort Appraisal (PR39 semantic: Intensity is Discomfort)
        list.Add(AppraiseComfort(experience.Comfort));

        // 6. Mood Appraisal
        list.Add(AppraiseMood(experience.Mood));

        return list;
    }

    private static CharacterAppraisal AppraiseHunger(HungerPerception hunger)
    {
        if (hunger.Level == HungerLevel.Satisfied)
        {
            return new CharacterAppraisal(
                AppraisalType.PhysicalRestoration,
                AppraisalPolarity.Neutral,
                0.0,
                AppraisalSource.Hunger
            );
        }

        return new CharacterAppraisal(
            AppraisalType.PhysicalDeprivation,
            AppraisalPolarity.Negative,
            hunger.Intensity.Value,
            AppraisalSource.Hunger
        );
    }

    private static CharacterAppraisal AppraiseEnergy(EnergyPerception energy)
    {
        if (energy.Level is EnergyLevel.Exhausted or EnergyLevel.Tired)
        {
            return new CharacterAppraisal(
                AppraisalType.Fatigue,
                AppraisalPolarity.Negative,
                energy.Intensity.Value,
                AppraisalSource.Energy
            );
        }

        if (energy.Level is EnergyLevel.Energized or EnergyLevel.HighlyEnergized)
        {
            double positiveIntensity = Math.Clamp((double)(energy.RawValue / 100.00m), 0.0, 1.0);
            return new CharacterAppraisal(
                AppraisalType.Recovery,
                AppraisalPolarity.Positive,
                positiveIntensity,
                AppraisalSource.Energy
            );
        }

        // Moderate
        return new CharacterAppraisal(
            AppraisalType.Fatigue,
            AppraisalPolarity.Neutral,
            0.0,
            AppraisalSource.Energy
        );
    }

    private static CharacterAppraisal AppraiseStress(StressPerception stress)
    {
        if (stress.Level == StressLevel.Calm)
        {
            return new CharacterAppraisal(
                AppraisalType.Safety,
                AppraisalPolarity.Neutral,
                0.0,
                AppraisalSource.Stress
            );
        }

        return new CharacterAppraisal(
            AppraisalType.StressPressure,
            AppraisalPolarity.Negative,
            stress.Intensity.Value,
            AppraisalSource.Stress
        );
    }

    private static CharacterAppraisal AppraiseSocialNeed(SocialNeedPerception social)
    {
        if (social.Level == SocialNeedLevel.SociallySatisfied)
        {
            return new CharacterAppraisal(
                AppraisalType.SocialConnection,
                AppraisalPolarity.Neutral,
                0.0,
                AppraisalSource.SocialNeed
            );
        }

        return new CharacterAppraisal(
            AppraisalType.SocialDeprivation,
            AppraisalPolarity.Negative,
            social.Intensity.Value,
            AppraisalSource.SocialNeed
        );
    }

    private static CharacterAppraisal AppraiseComfort(ComfortPerception comfort)
    {
        if (comfort.Level is ComfortLevel.VeryUncomfortable or ComfortLevel.Uncomfortable)
        {
            return new CharacterAppraisal(
                AppraisalType.Discomfort,
                AppraisalPolarity.Negative,
                comfort.Intensity.Value,
                AppraisalSource.Comfort
            );
        }

        if (comfort.Level is ComfortLevel.Comfortable or ComfortLevel.VeryComfortable)
        {
            double comfortIntensity = Math.Clamp((double)(comfort.RawValue / 100.00m), 0.0, 1.0);
            return new CharacterAppraisal(
                AppraisalType.Comfort,
                AppraisalPolarity.Positive,
                comfortIntensity,
                AppraisalSource.Comfort
            );
        }

        // Neutral
        return new CharacterAppraisal(
            AppraisalType.Comfort,
            AppraisalPolarity.Neutral,
            0.0,
            AppraisalSource.Comfort
        );
    }

    private static CharacterAppraisal AppraiseMood(MoodPerception mood)
    {
        if (mood.Level is MoodPerceptionLevel.Depressed or MoodPerceptionLevel.Low)
        {
            double negIntensity = Math.Clamp((double)((100.00m - mood.RawValue) / 100.00m), 0.0, 1.0);
            return new CharacterAppraisal(
                AppraisalType.NegativeMood,
                AppraisalPolarity.Negative,
                negIntensity,
                AppraisalSource.Mood
            );
        }

        if (mood.Level is MoodPerceptionLevel.Good or MoodPerceptionLevel.Elated)
        {
            return new CharacterAppraisal(
                AppraisalType.PositiveMood,
                AppraisalPolarity.Positive,
                mood.Intensity.Value,
                AppraisalSource.Mood
            );
        }

        // Neutral
        return new CharacterAppraisal(
            AppraisalType.PositiveMood,
            AppraisalPolarity.Neutral,
            0.0,
            AppraisalSource.Mood
        );
    }

    private static CharacterAppraisal SelectDominantAppraisal(IReadOnlyList<CharacterAppraisal> appraisals)
    {
        static int SourcePrecedence(AppraisalSource source) => source switch
        {
            AppraisalSource.Stress => 1,
            AppraisalSource.Hunger => 2,
            AppraisalSource.SocialNeed => 3,
            AppraisalSource.Energy => 4,
            AppraisalSource.Comfort => 5,
            AppraisalSource.Mood => 6,
            _ => 99
        };

        // 1. Highest Intensity wins across ALL appraisals (Positive, Negative, or Neutral)
        // 2. If equal intensity -> deterministic precedence: Stress > Hunger > SocialNeed > Energy > Comfort > Mood
        // 3. If all intensities are 0 -> first by precedence (Neutral)
        CharacterAppraisal best = appraisals[0];

        for (int i = 1; i < appraisals.Count; i++)
        {
            var candidate = appraisals[i];

            if (candidate.Intensity > best.Intensity)
            {
                best = candidate;
            }
            else if (candidate.Intensity == best.Intensity)
            {
                if (SourcePrecedence(candidate.Source) < SourcePrecedence(best.Source))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }
}
