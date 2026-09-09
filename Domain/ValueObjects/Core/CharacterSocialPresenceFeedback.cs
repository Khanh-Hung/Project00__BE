using System;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable feedback record representing the outcome of social presence mutation
/// following action execution in a cognitive cycle.
/// </summary>
public sealed record CharacterSocialPresenceFeedback(
    Guid PresenceId,
    Guid CharacterId,
    Guid ExecutionId,
    SocialPresenceStatus Status,
    LifeActivityType CurrentActivityType,
    RelationshipTargetType? TargetType,
    Guid? TargetId,
    DateTimeOffset UpdatedAtUtc,
    bool IsSuccess,
    string? FailureReason = null
);
