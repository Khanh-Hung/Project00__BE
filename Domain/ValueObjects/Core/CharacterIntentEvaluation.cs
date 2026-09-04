using System;

namespace Domain.ValueObjects;

public sealed record CharacterIntentEvaluation
{
    public Guid CharacterId { get; init; }
    public int StateVersion { get; init; }
    public CharacterIntent? Intent { get; init; }
    public DateTimeOffset EvaluatedAtUtc { get; init; }

    public CharacterIntentEvaluation(
        Guid characterId,
        int stateVersion,
        CharacterIntent? intent,
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
        Intent = intent;
        EvaluatedAtUtc = evaluatedAtUtc;
    }
}
