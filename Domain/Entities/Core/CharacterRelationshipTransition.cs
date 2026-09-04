using System;
using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Authoritative relationship transition ledger entry.
/// Guarantees idempotency via database unique constraint on (CharacterId, ExecutionId)
/// and payload consistency verification via TransitionFingerprint.
/// </summary>
public class CharacterRelationshipTransition : Entity
{
    public Guid CharacterId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public Guid TargetId { get; private set; }
    public RelationshipTargetType TargetType { get; private set; }
    public string TransitionFingerprint { get; private set; } = string.Empty;

    public int TrustDelta { get; private set; }
    public int AffectionDelta { get; private set; }
    public int FamiliarityDelta { get; private set; }

    public RelationshipType OldRelationshipType { get; private set; }
    public RelationshipType NewRelationshipType { get; private set; }

    public uint VersionBefore { get; private set; }
    public uint VersionAfter { get; private set; }
    public string? Reason { get; private set; }
    public DateTime AppliedAtUtc { get; private set; }

    private CharacterRelationshipTransition() : base() { }

    public CharacterRelationshipTransition(
        Guid characterId,
        Guid executionId,
        Guid targetId,
        RelationshipTargetType targetType,
        int trustDelta,
        int affectionDelta,
        int familiarityDelta,
        RelationshipType oldRelationshipType,
        RelationshipType newRelationshipType,
        uint versionBefore,
        uint versionAfter,
        string? reason,
        DateTime appliedAtUtc) : base()
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        if (executionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));
        if (targetId == Guid.Empty)
            throw new ArgumentException("TargetId cannot be empty.", nameof(targetId));

        CharacterId = characterId;
        ExecutionId = executionId;
        TargetId = targetId;
        TargetType = targetType;
        TrustDelta = trustDelta;
        AffectionDelta = affectionDelta;
        FamiliarityDelta = familiarityDelta;
        OldRelationshipType = oldRelationshipType;
        NewRelationshipType = newRelationshipType;
        VersionBefore = versionBefore;
        VersionAfter = versionAfter;
        Reason = reason?.Trim();
        AppliedAtUtc = appliedAtUtc;

        TransitionFingerprint = CanonicalRelationshipFingerprint.Compute(
            characterId,
            executionId,
            targetId,
            targetType,
            trustDelta,
            affectionDelta,
            familiarityDelta,
            newRelationshipType != oldRelationshipType ? newRelationshipType : null);
    }
}
