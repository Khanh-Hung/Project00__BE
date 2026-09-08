using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Domain.Common;

/// <summary>
/// Computes canonical SHA-256 deterministic fingerprint for WorldCognitiveEvent.
/// Captures all factual semantic attributes influencing cognitive perception.
/// </summary>
public static class CanonicalWorldEventFingerprint
{
    public const int SchemaVersion = 1;

    public static string Compute(
        Guid eventId,
        Guid characterId,
        DateTimeOffset occurredAtUtc,
        string eventName,
        string source,
        string? category = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (eventId == Guid.Empty)
            throw new ArgumentException("EventId cannot be empty.", nameof(eventId));

        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        var canonical = string.Join(
            "|",
            SchemaVersion.ToString(CultureInfo.InvariantCulture),
            eventId.ToString("D"),
            characterId.ToString("D"),
            occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            source.Trim(),
            eventName.Trim(),
            (category ?? string.Empty).Trim()
        );

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }
}
