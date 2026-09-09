using System;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Default deterministic rule-based social behavior policy.
/// Evaluates social presence, active goals, relationship context, and personality
/// to propose actionable social decisions without LLM, ML, or probabilistic heuristics.
/// </summary>
public sealed class SocialBehaviorPolicy : ISocialBehaviorPolicy
{
    public SocialBehaviorDecision? Evaluate(
        Guid characterId,
        CharacterSocialPresenceContext? socialPresenceContext,
        CharacterRelationshipContext? relationshipContext,
        CharacterGoalContext? goalContext,
        CharacterPersonalitySnapshot? personalitySnapshot)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (socialPresenceContext == null)
            return null;

        // Rule 1: Offline characters cannot engage in social interactions
        if (socialPresenceContext.Status == SocialPresenceStatus.Offline)
        {
            return new SocialBehaviorDecision(
                ShouldAct: false,
                ProposedAction: ActionType.Socialize,
                Reason: "Character is offline."
            );
        }

        // Rule 2: Away characters do not initiate new social actions
        if (socialPresenceContext.Status == SocialPresenceStatus.Away)
        {
            return new SocialBehaviorDecision(
                ShouldAct: false,
                ProposedAction: ActionType.Socialize,
                Reason: "Character is away."
            );
        }

        // Rule 3: Active presence with active social goal
        bool hasActiveSocialGoal = goalContext != null &&
            goalContext.Status == CharacterGoalStatus.Active &&
            (goalContext.GoalKey.Equals("Socialize", StringComparison.OrdinalIgnoreCase) ||
             goalContext.GoalKey.Equals("BuildRelationship", StringComparison.OrdinalIgnoreCase));

        if (hasActiveSocialGoal)
        {
            double intensityBonus = 0.10;
            if (personalitySnapshot != null && (personalitySnapshot.SocialConfidence > 60 || personalitySnapshot.Warmth > 60))
            {
                intensityBonus = 0.15;
            }

            bool hasRelationshipTarget = relationshipContext != null && relationshipContext.TargetId != Guid.Empty;

            if (hasRelationshipTarget)
            {
                return new SocialBehaviorDecision(
                    ShouldAct: true,
                    ProposedAction: ActionType.Socialize,
                    IntensityBonus: intensityBonus,
                    TargetType: relationshipContext!.TargetType,
                    TargetId: relationshipContext.TargetId,
                    Reason: "Active social goal with available relationship target."
                );
            }

            return new SocialBehaviorDecision(
                ShouldAct: true,
                ProposedAction: ActionType.Socialize,
                IntensityBonus: intensityBonus,
                TargetType: null,
                TargetId: null,
                Reason: "Active social goal without specific target."
            );
        }

        // Rule 4: Ongoing socializing activity reinforcement
        if (socialPresenceContext.CurrentActivityType == LifeActivityType.Socialize)
        {
            return new SocialBehaviorDecision(
                ShouldAct: true,
                ProposedAction: ActionType.Socialize,
                IntensityBonus: 0.05,
                TargetType: socialPresenceContext.TargetType,
                TargetId: socialPresenceContext.TargetId,
                Reason: "Reinforcing ongoing social activity."
            );
        }

        return null;
    }
}
