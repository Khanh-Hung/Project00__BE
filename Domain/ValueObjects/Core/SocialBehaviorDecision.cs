using System;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable decision result produced by ISocialBehaviorPolicy.
/// Pure, deterministic output indicating proposed social behavior.
/// </summary>
public sealed record SocialBehaviorDecision(
    bool ShouldAct,
    ActionType ProposedAction,
    double IntensityBonus = 0.0,
    RelationshipTargetType? TargetType = null,
    Guid? TargetId = null,
    string? Reason = null
);
