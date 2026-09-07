using System;
using Application.Contracts.CognitiveCycle;
using Domain.Common;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Services.CognitiveCycle;

/// <summary>
/// Authoritative default policy deriving personality adaptation evidence from cognitive cycle outcomes.
/// Invariant: Infrastructure failures, cancelled actions, or completed without action produce ZERO evidence.
/// Invariant: Only semantically meaningful, successful cognitive actions produce evidence.
/// </summary>
public sealed class DefaultPersonalityAdaptationPolicy : IPersonalityAdaptationPolicy
{
    public PersonalityAdaptationProposal? Evaluate(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        // 1. Strict status eligibility: Only CompletedWithAction qualifies
        if (result.Status != CharacterCognitiveCycleStatus.CompletedWithAction)
        {
            return null;
        }

        // 2. Action execution must have succeeded
        if (result.ActionExecution == null || !result.ActionExecution.IsSuccess)
        {
            return null;
        }

        // 3. Social Evidence: Derived if relationship feedback occurred
        if (result.RelationshipFeedback != null)
        {
            var rel = result.RelationshipFeedback;

            if (rel.TrustDelta > 0 || rel.AffectionDelta > 0)
            {
                return new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
                    traitKey: PersonalityTraitKeys.Warmth,
                    direction: +1,
                    strength: 1,
                    reason: "Positive social outcome with interaction partner."
                );
            }

            if (rel.TrustDelta < 0 || rel.AffectionDelta < 0)
            {
                return new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.NegativeSocialOutcome,
                    traitKey: PersonalityTraitKeys.Warmth,
                    direction: -1,
                    strength: 1,
                    reason: "Negative social outcome or conflict with interaction partner."
                );
            }

            if (rel.FamiliarityDelta > 0)
            {
                return new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
                    traitKey: PersonalityTraitKeys.SocialConfidence,
                    direction: +1,
                    strength: 1,
                    reason: "Consistent social engagement building familiarity."
                );
            }
        }

        // 4. Autonomous / Self-regulation Evidence
        if (result.Emotion != null &&
            (result.Emotion.Type is EmotionType.Stress or EmotionType.Fatigue) &&
            result.Emotion.Intensity >= 0.50)
        {
            return new PersonalityAdaptationProposal(
                evidenceType: PersonalityAdaptationEvidenceType.EmotionalRegulation,
                traitKey: PersonalityTraitKeys.EmotionalStability,
                direction: +1,
                strength: 1,
                reason: "Successfully executed purposeful action under emotional fatigue or stress."
            );
        }

        // 5. General purposeful behavioral consistency
        return new PersonalityAdaptationProposal(
            evidenceType: PersonalityAdaptationEvidenceType.BehavioralConsistency,
            traitKey: PersonalityTraitKeys.Conscientiousness,
            direction: +1,
            strength: 1,
            reason: "Deliberate action execution consistent with formed intent."
        );
    }
}
