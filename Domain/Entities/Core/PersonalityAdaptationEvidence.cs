using System;
using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Domain entity representing a discrete, immutable piece of behavioral evidence derived from a cognitive cycle.
/// Evidence units accumulate until an explicit threshold is reached, driving gradual trait adaptation.
/// </summary>
public sealed class PersonalityAdaptationEvidence
{
    public Guid Id { get; private set; }
    public Guid CharacterId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public PersonalityAdaptationEvidenceType EvidenceType { get; private set; }
    public string TraitKey { get; private set; } = string.Empty;
    public int Direction { get; private set; }
    public int Strength { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string Fingerprint { get; private set; } = string.Empty;
    public bool IsApplied { get; private set; }
    public Guid? AdaptationId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private PersonalityAdaptationEvidence() { } // EF Core

    public PersonalityAdaptationEvidence(
        Guid characterId,
        Guid executionId,
        PersonalityAdaptationEvidenceType evidenceType,
        string traitKey,
        int direction,
        int strength,
        string reason,
        string fingerprint,
        DateTime? createdAtUtc = null,
        Guid? id = null)
    {
        if (characterId == Guid.Empty) throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        if (executionId == Guid.Empty) throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("Fingerprint cannot be empty.", nameof(fingerprint));

        var normalizedTraitKey = PersonalityTraitKeys.Normalize(traitKey);

        if (direction != 1 && direction != -1)
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Direction must be either +1 or -1.");

        if (strength <= 0)
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "Strength must be greater than 0.");

        Id = id ?? Guid.NewGuid();
        CharacterId = characterId;
        ExecutionId = executionId;
        EvidenceType = evidenceType;
        TraitKey = normalizedTraitKey;
        Direction = direction;
        Strength = strength;
        Reason = reason ?? string.Empty;
        Fingerprint = fingerprint;
        IsApplied = false;
        AdaptationId = null;
        CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow;
    }

    public void MarkApplied(Guid adaptationId)
    {
        if (adaptationId == Guid.Empty)
            throw new ArgumentException("AdaptationId cannot be empty.", nameof(adaptationId));

        IsApplied = true;
        AdaptationId = adaptationId;
    }
}
