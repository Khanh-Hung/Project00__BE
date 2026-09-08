using System;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable cognitive snapshot representing the character's active goal.
/// Read-only value object passed into the cognitive pipeline.
/// Invariant: Strictly factual goal snapshot. Never mutates CharacterState or unrelated subsystems.
/// </summary>
public sealed record CharacterGoalContext(
    Guid GoalId,
    string GoalKey,
    CharacterGoalStatus Status,
    int Priority,
    int Progress
);
