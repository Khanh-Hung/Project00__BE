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

        var intentType = MapDesireToIntent(dominantDesire.Type);

        var intent = new CharacterIntent(
            type: intentType,
            intensity: dominantDesire.Intensity,
            sourceDesire: dominantDesire.Type,
            motivation: dominantDesire.Motivation.Type,
            stateVersion: desireEvaluation.StateVersion
        );

        return new CharacterIntentEvaluation(
            characterId: desireEvaluation.CharacterId,
            stateVersion: desireEvaluation.StateVersion,
            intent: intent,
            evaluatedAtUtc: context.EvaluatedAtUtc
        );
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
