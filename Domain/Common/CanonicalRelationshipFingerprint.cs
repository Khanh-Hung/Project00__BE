using System;
using System.Security.Cryptography;
using System.Text;
using Domain.Enums;

namespace Domain.Common;

/// <summary>
/// Computes canonical deterministic SHA-256 fingerprints for relationship feedback and transitions.
/// Used for replay detection and idempotency conflict verification.
/// </summary>
public static class CanonicalRelationshipFingerprint
{
    private const string CurrentSchemaVersion = "v1";

    public static string Compute(
        Guid characterId,
        Guid executionId,
        Guid targetId,
        RelationshipTargetType targetType,
        int trustDelta,
        int affectionDelta,
        int familiarityDelta,
        RelationshipType? newRelationshipType)
    {
        // Canonical payload string: schemaVersion|charId|execId|targetType|targetId|trustDelta|affectionDelta|famDelta|newType
        var canonicalString = string.Join("|",
            CurrentSchemaVersion,
            characterId.ToString("D"),
            executionId.ToString("D"),
            ((int)targetType).ToString(),
            targetId.ToString("D"),
            trustDelta.ToString(),
            affectionDelta.ToString(),
            familiarityDelta.ToString(),
            newRelationshipType.HasValue ? ((int)newRelationshipType.Value).ToString() : "none"
        );

        var bytes = Encoding.UTF8.GetBytes(canonicalString);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
