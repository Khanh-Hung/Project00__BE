using System;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable cognitive snapshot representing the character's authoritative social presence.
/// Read-only value object passed into the cognitive pipeline.
/// </summary>
public sealed record CharacterSocialPresenceContext(
    Guid PresenceId,
    Guid CharacterId,
    SocialPresenceStatus Status,
    LifeActivityType CurrentActivityType,
    SocialPresenceVisibility Visibility,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RelationshipTargetType? TargetType = null,
    Guid? TargetId = null,
    uint Version = 1
);
