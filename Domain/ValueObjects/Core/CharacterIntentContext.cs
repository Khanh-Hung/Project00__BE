using System;

namespace Domain.ValueObjects;

public sealed record CharacterIntentContext
{
    public DateTimeOffset EvaluatedAtUtc { get; init; }

    public CharacterIntentContext(DateTimeOffset evaluatedAtUtc)
    {
        EvaluatedAtUtc = evaluatedAtUtc;
    }
}
