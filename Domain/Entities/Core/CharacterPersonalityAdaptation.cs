using System;
using Domain.Common;

namespace Domain.Entities;

/// <summary>
/// Audit ledger entity recording an applied long-term personality adaptation step.
/// Distinct from CharacterStateTransition and CharacterRelationshipTransition.
/// </summary>
public sealed class CharacterPersonalityAdaptation
{
    public Guid Id { get; private set; }
    public Guid CharacterId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public string TraitKey { get; private set; } = string.Empty;
    public int ValueBefore { get; private set; }
    public int ValueAfter { get; private set; }
    public int Delta { get; private set; }
    public int EvidenceCount { get; private set; }
    public string Fingerprint { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    private CharacterPersonalityAdaptation() { } // EF Core

    public CharacterPersonalityAdaptation(
        Guid characterId,
        Guid executionId,
        string traitKey,
        int valueBefore,
        int valueAfter,
        int delta,
        int evidenceCount,
        string fingerprint,
        DateTime? createdAtUtc = null,
        Guid? id = null)
    {
        if (characterId == Guid.Empty) throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        if (executionId == Guid.Empty) throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("Fingerprint cannot be empty.", nameof(fingerprint));

        var normalizedTraitKey = PersonalityTraitKeys.Normalize(traitKey);

        Id = id ?? Guid.NewGuid();
        CharacterId = characterId;
        ExecutionId = executionId;
        TraitKey = normalizedTraitKey;
        ValueBefore = valueBefore;
        ValueAfter = valueAfter;
        Delta = delta;
        EvidenceCount = evidenceCount;
        Fingerprint = fingerprint;
        CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow;
    }
}
