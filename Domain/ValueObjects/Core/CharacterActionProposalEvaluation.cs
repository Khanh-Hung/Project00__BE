using System;

namespace Domain.ValueObjects;

public sealed record CharacterActionProposalEvaluation
{
    public Guid CharacterId { get; init; }
    public int StateVersion { get; init; }
    public CharacterActionProposal? Proposal { get; init; }
    public DateTimeOffset EvaluatedAtUtc { get; init; }

    public CharacterActionProposalEvaluation(
        Guid characterId,
        int stateVersion,
        CharacterActionProposal? proposal,
        DateTimeOffset evaluatedAtUtc)
    {
        if (characterId == Guid.Empty)
        {
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        }

        if (stateVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateVersion), stateVersion, "StateVersion cannot be negative.");
        }

        CharacterId = characterId;
        StateVersion = stateVersion;
        Proposal = proposal;
        EvaluatedAtUtc = evaluatedAtUtc;
    }
}
