using System;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable cognitive snapshot representing the character's relationship toward a specific target entity.
/// Read-only value object passed into the cognitive pipeline.
/// </summary>
public sealed record CharacterRelationshipContext(
    Guid TargetId,
    RelationshipTargetType TargetType,
    RelationshipType RelationshipType,
    int Trust,
    int Affection,
    int Familiarity
);
