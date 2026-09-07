using System;
using System.Security.Cryptography;
using System.Text;
using Domain.Enums;

namespace Domain.Common;

/// <summary>
/// Computes canonical deterministic SHA-256 fingerprints representing the factual semantic payload
/// of a LifeSimulation/WorldEvent outbox message.
/// Invariants:
/// - Must not include volatile retry count, error messages, or unformatted text.
/// - Description is intentionally treated as diagnostic/human-readable and excluded from canonical semantic fingerprinting.
/// - Used for strict idempotency boundary:
///   * Same EventId + same semantic fingerprint => idempotent replay
///   * Same EventId + different semantic fingerprint => idempotency conflict
/// </summary>
public static class CanonicalOutboxFingerprint
{
    public const string SchemaVersion = "v1";

    public static string Compute(
        Guid eventId,
        Guid characterId,
        string eventType,
        Guid activityId,
        LifeActivityType activityType,
        DateTime occurredAtUtc)
    {
        var canonicalString = string.Join("|",
            SchemaVersion,
            eventId.ToString("D"),
            characterId.ToString("D"),
            eventType.Trim(),
            activityId.ToString("D"),
            ((int)activityType).ToString(),
            occurredAtUtc.ToString("O")
        );

        var bytes = Encoding.UTF8.GetBytes(canonicalString);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
