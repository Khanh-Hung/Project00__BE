using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Value object representing an immutable snapshot of a character goal.
/// </summary>
public sealed record GoalSnapshot(
    Guid GoalId,
    Guid CharacterId,
    string Title,
    CharacterGoalType GoalType,
    CharacterGoalPriority Priority,
    CharacterGoalStatus Status,
    float Progress,
    double CurrentValue,
    double TargetValue,
    string? CurrentMilestone = null,
    float MilestoneProgress = 0f,
    string? Description = null
);
