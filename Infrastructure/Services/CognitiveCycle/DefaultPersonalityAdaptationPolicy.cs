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

            // Affection changes explicitly map to Warmth
            if (rel.AffectionDelta > 0)
            {
                return new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
                    traitKey: PersonalityTraitKeys.Warmth,
                    direction: +1,
                    strength: 1,
                    reason: "Affectionate social outcome with interaction partner."
                );
            }

            if (rel.AffectionDelta < 0)
            {
                return new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.NegativeSocialOutcome,
                    traitKey: PersonalityTraitKeys.Warmth,
                    direction: -1,
                    strength: 1,
                    reason: "Antipathy or conflict with interaction partner reducing warmth."
                );
            }

            // Trust changes explicitly map to TrustDisposition
            if (rel.TrustDelta > 0)
            {
                return new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
                    traitKey: PersonalityTraitKeys.TrustDisposition,
                    direction: +1,
                    strength: 1,
                    reason: "Demonstrated reliability reinforcing trust disposition."
                );
            }

            if (rel.TrustDelta < 0)
            {
                return new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.RepeatedConflict,
                    traitKey: PersonalityTraitKeys.TrustDisposition,
                    direction: -1,
                    strength: 1,
                    reason: "Breach of trust or conflict undermining trust disposition."
                );
            }

            // Familiarity changes map to SocialConfidence
            if (rel.FamiliarityDelta > 0)
            {
                return new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
                    traitKey: PersonalityTraitKeys.SocialConfidence,
                    direction: +1,
                    strength: 1,
                    reason: "Consistent social engagement building social confidence."
                );
            }
        }

        // 4. Emotional Regulation: Requires deliberate stress reduction behavior under stress
        var isElevatedStress = (result.Emotion != null && result.Emotion.Type == EmotionType.Stress && result.Emotion.Intensity >= 0.40) ||
                               (result.Experience != null && result.Experience.Stress.Level >= StressLevel.MildPressure);

        if (isElevatedStress &&
            result.ActionProposal?.Proposal?.Type == ActionType.ReduceStress &&
            result.ActionExecution.AppliedDelta != null &&
            result.ActionExecution.AppliedDelta.StressDelta < 0)
        {
            return new PersonalityAdaptationProposal(
                evidenceType: PersonalityAdaptationEvidenceType.EmotionalRegulation,
                traitKey: PersonalityTraitKeys.EmotionalStability,
                direction: +1,
                strength: 1,
                reason: "Successfully executed deliberate stress regulation under pressure."
            );
        }

        // Generic actions (Eat, Rest, Walk) without explicit semantic evidence produce no personality learning
        return null;
    }
}
