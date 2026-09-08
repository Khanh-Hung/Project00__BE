using System;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that transforms a CharacterDesireEvaluation
/// into an actionable CharacterIntentEvaluation.
/// Zero side-effects, zero LLM, zero DB, zero random, zero clock.
/// </summary>
public sealed class CharacterIntentPolicy : ICharacterIntentPolicy
{
    public CharacterIntentEvaluation Evaluate(
        CharacterDesireEvaluation desireEvaluation,
        CharacterIntentContext context)
    {
        ArgumentNullException.ThrowIfNull(desireEvaluation, nameof(desireEvaluation));
        ArgumentNullException.ThrowIfNull(context, nameof(context));

        var dominantDesire = desireEvaluation.DominantDesire;

        // If no meaningful desire exists (all intensities = 0), intent is absent (null)
        if (dominantDesire == null || dominantDesire.Intensity <= 0.0)
        {
            return new CharacterIntentEvaluation(
                characterId: desireEvaluation.CharacterId,
                stateVersion: desireEvaluation.StateVersion,
                intent: null,
                evaluatedAtUtc: context.EvaluatedAtUtc
            );
        }

        var selectedDesire = dominantDesire;
        var goal = context.GoalContext;

        if (goal != null && goal.Status == CharacterGoalStatus.Active)
        {
            var alignedDesireType = MapGoalToDesire(goal.GoalKey);
            if (alignedDesireType.HasValue)
            {
                var alignedDesire = desireEvaluation.Desires
                    .FirstOrDefault(d => d.Type == alignedDesireType.Value && d.Intensity > 0.0);

                if (alignedDesire != null)
                {
                    selectedDesire = alignedDesire;
                }
            }
        }

        double effectiveIntensity = selectedDesire.Intensity;
        if (goal != null && goal.Status == CharacterGoalStatus.Active)
        {
            var alignedDesireType = MapGoalToDesire(goal.GoalKey);
            if (alignedDesireType.HasValue && selectedDesire.Type == alignedDesireType.Value)
            {
                effectiveIntensity = Math.Min(1.0, effectiveIntensity + 0.1);
            }
        }

        var intentType = MapDesireToIntent(selectedDesire.Type);

        var intent = new CharacterIntent(
            type: intentType,
            intensity: effectiveIntensity,
            sourceDesire: selectedDesire.Type,
            motivation: selectedDesire.Motivation.Type,
            stateVersion: desireEvaluation.StateVersion
        );

        return new CharacterIntentEvaluation(
            characterId: desireEvaluation.CharacterId,
            stateVersion: desireEvaluation.StateVersion,
            intent: intent,
            evaluatedAtUtc: context.EvaluatedAtUtc
        );
    }

    private static DesireType? MapGoalToDesire(string goalKey)
    {
        if (string.Equals(goalKey, "BuildRelationship", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(goalKey, "Socialize", StringComparison.OrdinalIgnoreCase))
            return DesireType.NeedSocialConnection;

        if (string.Equals(goalKey, "Rest", StringComparison.OrdinalIgnoreCase))
            return DesireType.NeedRest;

        if (string.Equals(goalKey, "Eat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(goalKey, "Food", StringComparison.OrdinalIgnoreCase))
            return DesireType.NeedFood;

        if (string.Equals(goalKey, "ReduceStress", StringComparison.OrdinalIgnoreCase))
            return DesireType.NeedReduceStress;

        return null;
    }

    private static IntentType MapDesireToIntent(DesireType desireType) =>
        desireType switch
        {
            DesireType.NeedFood => IntentType.SeekFood,
            DesireType.NeedRest => IntentType.SeekRest,
            DesireType.NeedReduceStress => IntentType.ReduceStress,
            DesireType.NeedSocialConnection => IntentType.SeekSocialConnection,
            DesireType.NeedComfort => IntentType.SeekComfort,
            DesireType.NeedSafety => IntentType.SeekSafety,
            _ => throw new ArgumentOutOfRangeException(nameof(desireType), desireType, "Unsupported desire type for intent formation.")
        };
}
