using System;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable cognitive result representing the relationship feedback persisted from an execution.
/// Contains structured outcome deltas, not free-form text parsing.
/// </summary>
public sealed record CharacterRelationshipFeedback(
    Guid RelationshipId,
    Guid CharacterId,
    Guid ExecutionId,
    Guid TargetId,
    RelationshipTargetType TargetType,
    int TrustDelta,
    int AffectionDelta,
    int FamiliarityDelta,
    RelationshipType? NewRelationshipType,
    string? Reason,
    DateTimeOffset OccurredAtUtc
);
