using System;
using Domain.Entities;
using Domain.Enums;

namespace Application.Contracts.Goals;

public enum GoalPolicyAction
{
    None = 0,
    ReuseExisting = 1,
    CreateNew = 2
}

public sealed record GoalCreationCandidate(
    string GoalKey,
    CharacterGoalType GoalType,
    int Priority,
    string? Description = null
);

public sealed record GoalPolicyDecision(
    GoalPolicyAction Action,
    CharacterGoal? SelectedGoal = null,
    GoalCreationCandidate? Candidate = null
)
{
    public static GoalPolicyDecision None => new(GoalPolicyAction.None);

    public static GoalPolicyDecision Reuse(CharacterGoal goal) =>
        new(GoalPolicyAction.ReuseExisting, SelectedGoal: goal);

    public static GoalPolicyDecision Create(GoalCreationCandidate candidate) =>
        new(GoalPolicyAction.CreateNew, Candidate: candidate);
}

public sealed record CharacterGoalProgressFeedback(
    Guid GoalId,
    Guid ExecutionId,
    int PreviousProgress,
    int NewProgress,
    CharacterGoalStatus Status,
    bool IsDuplicateExecution
);
