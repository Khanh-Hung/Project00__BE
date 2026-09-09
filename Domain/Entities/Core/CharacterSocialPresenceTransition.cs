using System;
using System.Security.Cryptography;
using System.Text;
using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Authoritative persistent audit and idempotency ledger entry for social presence mutations
/// triggered by cognitive cycle action executions.
/// Enforces unique boundary on (CharacterId, ExecutionId) with OperationFingerprint verification.
/// </summary>
public sealed class CharacterSocialPresenceTransition : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public string ActionType { get; private set; }
    public SocialPresenceStatus OldStatus { get; private set; }
    public SocialPresenceStatus NewStatus { get; private set; }
    public LifeActivityType OldActivityType { get; private set; }
    public LifeActivityType NewActivityType { get; private set; }
    public RelationshipTargetType? TargetType { get; private set; }
    public Guid? TargetId { get; private set; }
    public uint VersionBefore { get; private set; }
    public uint VersionAfter { get; private set; }
    public string OperationFingerprint { get; private set; }
    public DateTime AppliedAtUtc { get; private set; }

    private CharacterSocialPresenceTransition() : base()
    {
        ActionType = null!;
        OperationFingerprint = null!;
    }

    public CharacterSocialPresenceTransition(
        Guid characterId,
        Guid executionId,
        string actionType,
        SocialPresenceStatus oldStatus,
        SocialPresenceStatus newStatus,
        LifeActivityType oldActivityType,
        LifeActivityType newActivityType,
        RelationshipTargetType? targetType,
        Guid? targetId,
        uint versionBefore,
        uint versionAfter,
        DateTimeOffset appliedAtUtc,
        Guid? id = null) : base(id ?? Guid.CreateVersion7())
    {
        if (characterId == Guid.Empty) throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        if (executionId == Guid.Empty) throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType, nameof(actionType));

        CharacterId = characterId;
        ExecutionId = executionId;
        ActionType = actionType.Trim();
        OldStatus = oldStatus;
        NewStatus = newStatus;
        OldActivityType = oldActivityType;
        NewActivityType = newActivityType;
        TargetType = targetType;
        TargetId = targetId;
        VersionBefore = versionBefore;
        VersionAfter = versionAfter;
        AppliedAtUtc = appliedAtUtc.UtcDateTime;
        OperationFingerprint = ComputeFingerprint(characterId, executionId, ActionType, targetType, targetId);
    }

    /// <summary>
    /// Computes deterministic SHA-256 fingerprint representing the complete semantic input of the social presence transition.
    /// <para>
    /// <b>Semantic Invariant:</b> Only caller-provided semantic inputs (CharacterId, ExecutionId, ActionType, TargetType, TargetId)
    /// are hashed into the fingerprint.
    /// </para>
    /// <para>
    /// <b>Derived State Exclusion:</b> Aggregate transition fields (OldStatus, NewStatus, OldActivityType, NewActivityType,
    /// VersionBefore, VersionAfter) are purely derived from the database state at the moment of mutation. They are not
    /// caller semantic intent, so they are intentionally excluded to ensure deterministic idempotency verification across retries.
    /// </para>
    /// </summary>
    public static string ComputeFingerprint(
        Guid characterId,
        Guid executionId,
        string actionType,
        RelationshipTargetType? targetType,
        Guid? targetId)
    {
        var raw = $"{characterId:D}:{executionId:D}:{actionType.Trim().ToUpperInvariant()}:{targetType?.ToString() ?? "None"}:{targetId?.ToString("D") ?? "None"}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
