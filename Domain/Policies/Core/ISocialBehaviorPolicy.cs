using System;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy evaluating social behavior decisions.
/// Zero side effects, zero I/O, zero random, zero clock.
/// </summary>
public interface ISocialBehaviorPolicy
{
    SocialBehaviorDecision? Evaluate(
        Guid characterId,
        CharacterSocialPresenceContext? socialPresenceContext,
        CharacterRelationshipContext? relationshipContext,
        CharacterGoalContext? goalContext,
        CharacterPersonalitySnapshot? personalitySnapshot);
}
