using System;

namespace Domain.ValueObjects;

public sealed record CharacterActionProposalContext
{
    public DateTimeOffset EvaluatedAtUtc { get; init; }
    public CharacterGoalContext? GoalContext { get; init; }

    public CharacterActionProposalContext(DateTimeOffset evaluatedAtUtc, CharacterGoalContext? goalContext = null)
    {
        EvaluatedAtUtc = evaluatedAtUtc;
        GoalContext = goalContext;
    }
}
