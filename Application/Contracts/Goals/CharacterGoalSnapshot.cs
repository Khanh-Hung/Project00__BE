using Domain.Enums;

namespace Application.Contracts.Goals;

public sealed record CharacterGoalSnapshot(
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
