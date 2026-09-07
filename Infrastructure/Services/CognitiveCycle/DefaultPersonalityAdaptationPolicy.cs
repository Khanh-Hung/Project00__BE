using System;
using System.Collections.Generic;
using Application.Contracts.CognitiveCycle;
using Domain.Common;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Services.CognitiveCycle;

/// <summary>
/// Authoritative default policy deriving personality adaptation evidence from cognitive cycle outcomes.
/// Invariant: Infrastructure failures, cancelled actions, or completed without action produce ZERO evidence.
/// Invariant: Only semantically meaningful, successful cognitive actions produce evidence.
/// Invariant: Returns an empty collection (never null) when no evidence is warranted.
/// Note: Mapping relationship deltas or stress regulation outcomes to personality adaptation proposals
/// is a domain policy heuristic representing long-term dispositions, not identity equality between states and traits.
/// Interpersonal relationships model dyadic bonds between specific characters, whereas personality traits represent
/// character-level aggregate behavioral tendencies across all environments and interactions.
/// </summary>
public sealed class DefaultPersonalityAdaptationPolicy : IPersonalityAdaptationPolicy
{
    public IReadOnlyList<PersonalityAdaptationProposal> Evaluate(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        // 1. Strict status eligibility: Only CompletedWithAction qualifies
        if (result.Status != CharacterCognitiveCycleStatus.CompletedWithAction)
        {
            return Array.Empty<PersonalityAdaptationProposal>();
        }

        // 2. Action execution must have succeeded
        if (result.ActionExecution == null || !result.ActionExecution.IsSuccess)
        {
            return Array.Empty<PersonalityAdaptationProposal>();
        }

        var proposals = new List<PersonalityAdaptationProposal>();

        // 3. Social Evidence: Derived if relationship feedback occurred
        if (result.RelationshipFeedback != null)
        {
            var rel = result.RelationshipFeedback;

            // Affection changes heuristically map to Warmth
            if (rel.AffectionDelta > 0)
            {
                proposals.Add(new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
                    traitKey: PersonalityTraitKeys.Warmth,
                    direction: +1,
                    strength: 1,
                    reason: "Affectionate social outcome with interaction partner."
                ));
            }
            else if (rel.AffectionDelta < 0)
            {
                proposals.Add(new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.NegativeSocialOutcome,
                    traitKey: PersonalityTraitKeys.Warmth,
                    direction: -1,
                    strength: 1,
                    reason: "Antipathy or conflict with interaction partner reducing warmth."
                ));
            }

            // Trust changes heuristically map to TrustDisposition
            if (rel.TrustDelta > 0)
            {
                proposals.Add(new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
                    traitKey: PersonalityTraitKeys.TrustDisposition,
                    direction: +1,
                    strength: 1,
                    reason: "Demonstrated reliability reinforcing trust disposition."
                ));
            }
            else if (rel.TrustDelta < 0)
            {
                proposals.Add(new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.RepeatedConflict,
                    traitKey: PersonalityTraitKeys.TrustDisposition,
                    direction: -1,
                    strength: 1,
                    reason: "Breach of trust or conflict undermining trust disposition."
                ));
            }

            // Familiarity changes heuristically map to SocialConfidence
            if (rel.FamiliarityDelta > 0)
            {
                proposals.Add(new PersonalityAdaptationProposal(
                    evidenceType: PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
                    traitKey: PersonalityTraitKeys.SocialConfidence,
                    direction: +1,
                    strength: 1,
                    reason: "Consistent social engagement building social confidence."
                ));
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
            proposals.Add(new PersonalityAdaptationProposal(
                evidenceType: PersonalityAdaptationEvidenceType.EmotionalRegulation,
                traitKey: PersonalityTraitKeys.EmotionalStability,
                direction: +1,
                strength: 1,
                reason: "Successfully executed deliberate stress regulation under pressure."
            ));
        }

        // Generic actions (Eat, Rest, Walk) without explicit semantic evidence produce no personality learning
        return proposals;
    }
}

