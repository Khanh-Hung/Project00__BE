using System;
using System.Security.Cryptography;
using System.Text;
using Domain.Enums;

namespace Domain.Common;

/// <summary>
/// Computes canonical deterministic SHA-256 fingerprints for personality adaptation evidence and adaptations.
/// Invariant: Must not include timestamps, random IDs, or volatile environment factors.
/// Used for replay detection and idempotency conflict verification.
/// </summary>
public static class CanonicalPersonalityFingerprint
{
    private const string CurrentSchemaVersion = "v1";

    public static string ComputeEvidence(
        Guid characterId,
        Guid executionId,
        PersonalityAdaptationEvidenceType evidenceType,
        string traitKey,
        int direction,
        int strength,
        string? canonicalReason)
    {
        var normalizedTraitKey = PersonalityTraitKeys.Normalize(traitKey);
        var normalizedReason = canonicalReason?.Trim() ?? string.Empty;

        var canonicalString = string.Join("|",
            CurrentSchemaVersion,
            characterId.ToString("D"),
            executionId.ToString("D"),
            ((int)evidenceType).ToString(),
            normalizedTraitKey,
            direction.ToString(),
            strength.ToString(),
            normalizedReason
        );

        var bytes = Encoding.UTF8.GetBytes(canonicalString);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeAdaptation(
        Guid characterId,
        Guid executionId,
        string traitKey,
        int valueBefore,
        int valueAfter,
        int delta,
        int evidenceCount)
    {
        var normalizedTraitKey = PersonalityTraitKeys.Normalize(traitKey);

        var canonicalString = string.Join("|",
            CurrentSchemaVersion,
            characterId.ToString("D"),
            executionId.ToString("D"),
            normalizedTraitKey,
            valueBefore.ToString(),
            valueAfter.ToString(),
            delta.ToString(),
            evidenceCount.ToString()
        );

        var bytes = Encoding.UTF8.GetBytes(canonicalString);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
