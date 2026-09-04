using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Common;

/// <summary>
/// Computes canonical deterministic SHA-256 fingerprints representing the semantic payload
/// of a cognitive cycle outcome feedback operation.
/// Used for strict semantic idempotency validation:
/// - same CharacterId + same ExecutionId + same semantic fingerprint = idempotent replay
/// - same CharacterId + same ExecutionId + different semantic fingerprint = idempotency conflict
/// </summary>
public static class CanonicalFeedbackFingerprint
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// Computes deterministic hex-encoded SHA-256 fingerprint from the canonical semantic components of feedback.
    /// </summary>
    public static string Compute(
        Guid characterId,
        Guid executionId,
        CharacterMemoryFeedbackType feedbackType,
        string canonicalContent)
    {
        ArgumentNullException.ThrowIfNull(canonicalContent);

        var canonical = string.Join(
            "|",
            SchemaVersion.ToString(CultureInfo.InvariantCulture),
            characterId.ToString("D"),
            executionId.ToString("D"),
            ((int)feedbackType).ToString(CultureInfo.InvariantCulture),
            canonicalContent.Trim()
        );

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }
}
