using System;

namespace Domain.ValueObjects;

public sealed record CharacterActionProposalContext
{
    public DateTimeOffset EvaluatedAtUtc { get; init; }

    public CharacterActionProposalContext(DateTimeOffset evaluatedAtUtc)
    {
        EvaluatedAtUtc = evaluatedAtUtc;
    }
}
