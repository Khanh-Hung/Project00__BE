using System;

namespace Domain.ValueObjects;

public sealed record CharacterIntentContext
{
    public DateTimeOffset EvaluatedAtUtc { get; init; }
    public CharacterGoalContext? GoalContext { get; init; }

    public CharacterIntentContext(DateTimeOffset evaluatedAtUtc, CharacterGoalContext? goalContext = null)
    {
        EvaluatedAtUtc = evaluatedAtUtc;
        GoalContext = goalContext;
    }
}
