using System;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that transforms a CharacterIntentEvaluation
/// into an actionable CharacterActionProposalEvaluation.
/// Zero side-effects, zero execution, zero LLM, zero DB, zero random, zero clock.
/// </summary>
public sealed class CharacterActionProposalPolicy : ICharacterActionProposalPolicy
{
    public CharacterActionProposalEvaluation Evaluate(
        CharacterIntentEvaluation intentEvaluation,
        CharacterActionProposalContext context)
    {
        ArgumentNullException.ThrowIfNull(intentEvaluation, nameof(intentEvaluation));
        ArgumentNullException.ThrowIfNull(context, nameof(context));

        var intent = intentEvaluation.Intent;

        // If intent is absent, action proposal is strictly null (no phantom actions)
        if (intent == null)
        {
            return new CharacterActionProposalEvaluation(
                characterId: intentEvaluation.CharacterId,
                stateVersion: intentEvaluation.StateVersion,
                proposal: null,
                evaluatedAtUtc: context.EvaluatedAtUtc
            );
        }

        var actionType = MapIntentToAction(intent.Type);

        double effectiveIntensity = intent.Intensity;
        var goal = context.GoalContext;
        if (goal != null && goal.Status == CharacterGoalStatus.Active && IsActionAlignedWithGoal(actionType, goal.GoalKey))
        {
            effectiveIntensity = Math.Min(1.0, effectiveIntensity + 0.05);
        }

        var socialDecision = context.SocialDecision;
        if (socialDecision != null && socialDecision.ShouldAct && socialDecision.ProposedAction == actionType)
        {
            effectiveIntensity = Math.Min(1.0, effectiveIntensity + socialDecision.IntensityBonus);
        }

        var proposal = new CharacterActionProposal(
            type: actionType,
            intensity: effectiveIntensity,
            sourceIntent: intent.Type,
            motivation: intent.Motivation,
            stateVersion: intent.StateVersion
        );

        return new CharacterActionProposalEvaluation(
            characterId: intentEvaluation.CharacterId,
            stateVersion: intentEvaluation.StateVersion,
            proposal: proposal,
            evaluatedAtUtc: context.EvaluatedAtUtc
        );
    }

    private static ActionType MapIntentToAction(IntentType intentType) =>
        intentType switch
        {
            IntentType.SeekFood => ActionType.Eat,
            IntentType.SeekRest => ActionType.Rest,
            IntentType.ReduceStress => ActionType.ReduceStress,
            IntentType.SeekSocialConnection => ActionType.Socialize,
            IntentType.SeekComfort => ActionType.SeekComfort,
            IntentType.SeekSafety => ActionType.SeekSafety,
            _ => throw new ArgumentOutOfRangeException(nameof(intentType), intentType, "Unsupported intent type for action proposal.")
        };

    private static bool IsActionAlignedWithGoal(ActionType actionType, string goalKey)
    {
        if (actionType == ActionType.Socialize &&
            (string.Equals(goalKey, "BuildRelationship", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(goalKey, "Socialize", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (actionType == ActionType.Rest && string.Equals(goalKey, "Rest", StringComparison.OrdinalIgnoreCase))
            return true;

        if (actionType == ActionType.Eat &&
            (string.Equals(goalKey, "Eat", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(goalKey, "Food", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (actionType == ActionType.ReduceStress && string.Equals(goalKey, "ReduceStress", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
