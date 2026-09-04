using System;
using System.Collections.Generic;

namespace Domain.ValueObjects;

public sealed record CharacterDesireEvaluation
{
    public Guid CharacterId { get; init; }
    public int StateVersion { get; init; }
    public IReadOnlyList<CharacterDesire> Desires { get; init; }
    public CharacterDesire DominantDesire { get; init; }
    public CharacterMotivation DominantMotivation => DominantDesire.Motivation;

    public CharacterDesireEvaluation(
        Guid characterId,
        int stateVersion,
        IReadOnlyList<CharacterDesire> desires,
        CharacterDesire dominantDesire)
    {
        if (characterId == Guid.Empty)
        {
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        }

        ArgumentNullException.ThrowIfNull(desires, nameof(desires));
        ArgumentNullException.ThrowIfNull(dominantDesire, nameof(dominantDesire));

        if (desires.Count == 0)
        {
            throw new ArgumentException("Desires list cannot be empty.", nameof(desires));
        }

        CharacterId = characterId;
        StateVersion = stateVersion;
        Desires = desires;
        DominantDesire = dominantDesire;
    }
}
