using System;
using Domain.Common;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable value object representing a proposed piece of personality adaptation evidence derived by adaptation policy.
/// </summary>
public sealed record PersonalityAdaptationProposal
{
    public PersonalityAdaptationEvidenceType EvidenceType { get; }
    public string TraitKey { get; }
    public int Direction { get; }
    public int Strength { get; }
    public string Reason { get; }

    public PersonalityAdaptationProposal(
        PersonalityAdaptationEvidenceType evidenceType,
        string traitKey,
        int direction,
        int strength,
        string reason)
    {
        TraitKey = PersonalityTraitKeys.Normalize(traitKey);

        if (direction != 1 && direction != -1)
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Direction must be either +1 or -1.");

        if (strength <= 0)
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "Strength must be greater than 0.");

        EvidenceType = evidenceType;
        Direction = direction;
        Strength = strength;
        Reason = reason ?? string.Empty;
    }
}
