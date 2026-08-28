using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Domain.Entities;

/// <summary>
/// Authoritative domain aggregate representing the complete visual state of a scene across turns.
/// Tracks characters, environment, props, persistent world changes, temporal validity across monotonic scene revisions,
/// and deterministic content fingerprint.
/// </summary>
public sealed class SceneVisualState
{
    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid CharacterId { get; private set; }
    public string SceneKey { get; private set; }
    public int SceneRevision { get; private set; } = 1;

    public string Location { get; private set; }
    public string TimeOfDay { get; private set; }
    public string Weather { get; private set; }
    public string Lighting { get; private set; }
    public string Atmosphere { get; private set; }

    public CharacterVisualState CharacterState { get; private set; }
    public IReadOnlyList<string> Props { get; private set; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> PersistentChanges { get; private set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Guid? ValidFromTurnId { get; private set; }
    public Guid? ValidUntilTurnId { get; private set; }
    public Guid? SourceTurnId { get; private set; }
    public int ValidFromRevision { get; private set; } = 1;
    public int? ValidUntilRevision { get; private set; }

    public string Fingerprint { get; private set; }
    public uint Version { get; private set; } = 1;
    public DateTime CreatedAt { get; private set; }

    [JsonConstructor]
    public SceneVisualState(
        Guid sessionId,
        Guid characterId,
        string location,
        CharacterVisualState characterState,
        int sceneRevision = 1,
        string? sceneKey = null,
        string? timeOfDay = null,
        string? weather = null,
        string? lighting = null,
        string? atmosphere = null,
        IReadOnlyList<string>? props = null,
        IReadOnlyDictionary<string, string>? persistentChanges = null,
        Guid? validFromTurnId = null,
        Guid? validUntilTurnId = null,
        Guid? sourceTurnId = null,
        int validFromRevision = 1,
        int? validUntilRevision = null,
        string? fingerprint = null,
        uint version = 1,
        Guid id = default,
        DateTime createdAt = default)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("SessionId cannot be empty.", nameof(sessionId));

        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(location))
            throw new ArgumentException("Location cannot be empty.", nameof(location));

        ArgumentNullException.ThrowIfNull(characterState, nameof(characterState));

        if (sceneRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(sceneRevision), "SceneRevision must be >= 1.");

        Id = id != Guid.Empty ? id : Guid.CreateVersion7();
        SessionId = sessionId;
        CharacterId = characterId;
        Location = location.Trim();
        SceneKey = !string.IsNullOrWhiteSpace(sceneKey) 
            ? sceneKey.Trim().ToLowerInvariant() 
            : NormalizeSceneKey(Location);

        CharacterState = characterState;
        SceneRevision = sceneRevision;

        TimeOfDay = !string.IsNullOrWhiteSpace(timeOfDay) ? timeOfDay.Trim() : "Daytime";
        Weather = !string.IsNullOrWhiteSpace(weather) ? weather.Trim() : "Clear";
        Lighting = !string.IsNullOrWhiteSpace(lighting) ? lighting.Trim() : "Natural diffused daylight";
        Atmosphere = !string.IsNullOrWhiteSpace(atmosphere) ? atmosphere.Trim() : "Neutral cinematic";

        Props = props?.Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList() 
                ?? (IReadOnlyList<string>)Array.Empty<string>();

        if (persistentChanges != null)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in persistentChanges)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    dict[kvp.Key.Trim()] = kvp.Value.Trim();
                }
            }
            PersistentChanges = dict;
        }
        else
        {
            PersistentChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        ValidFromTurnId = validFromTurnId;
        ValidUntilTurnId = validUntilTurnId;
        SourceTurnId = sourceTurnId ?? validFromTurnId;
        ValidFromRevision = validFromRevision > 0 ? validFromRevision : sceneRevision;
        ValidUntilRevision = validUntilRevision;
        Version = version;
        CreatedAt = createdAt != default ? createdAt : DateTime.UtcNow;

        Fingerprint = !string.IsNullOrWhiteSpace(fingerprint)
            ? fingerprint
            : ComputeFingerprint(CharacterId, Location, CharacterState.Outfit, CharacterState.Hairstyle, CharacterState.Pose,
                CharacterState.Action, TimeOfDay, Weather, Lighting, Atmosphere, Props, PersistentChanges, SceneRevision);
    }

    public static string NormalizeSceneKey(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return "default_scene";
        var norm = location.Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in norm)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == ' ' || c == '-' || c == '_') sb.Append('_');
        }
        var result = sb.ToString();
        while (result.Contains("__")) result = result.Replace("__", "_");
        return string.IsNullOrWhiteSpace(result) ? "default_scene" : result.Trim('_');
    }

    public static string ComputeFingerprint(
        Guid characterId,
        string location,
        string? outfit,
        string? hairstyle,
        string? pose,
        string? action,
        string timeOfDay,
        string weather,
        string lighting,
        string atmosphere,
        IEnumerable<string> props,
        IReadOnlyDictionary<string, string> persistentChanges,
        int sceneRevision)
    {
        var raw = new StringBuilder();
        raw.Append(characterId).Append('|');
        raw.Append(location.Trim().ToLowerInvariant()).Append('|');
        raw.Append(outfit?.Trim().ToLowerInvariant() ?? "").Append('|');
        raw.Append(hairstyle?.Trim().ToLowerInvariant() ?? "").Append('|');
        raw.Append(pose?.Trim().ToLowerInvariant() ?? "").Append('|');
        raw.Append(action?.Trim().ToLowerInvariant() ?? "").Append('|');
        raw.Append(timeOfDay.Trim().ToLowerInvariant()).Append('|');
        raw.Append(weather.Trim().ToLowerInvariant()).Append('|');
        raw.Append(lighting.Trim().ToLowerInvariant()).Append('|');
        raw.Append(atmosphere.Trim().ToLowerInvariant()).Append('|');
        raw.Append(sceneRevision).Append('|');

        // Deterministic sorted props
        var sortedProps = props.Select(p => p.Trim().ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal);
        raw.Append(string.Join(",", sortedProps)).Append('|');

        // Deterministic sorted persistent world changes (canonical key ordering)
        var sortedChanges = persistentChanges
            .OrderBy(kvp => kvp.Key.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key.Trim().ToLowerInvariant()}={kvp.Value.Trim().ToLowerInvariant()}");
        raw.Append(string.Join(";", sortedChanges));

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToString()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public void ApplyWorldMutation(string item, string state, Guid turnId, int revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item, nameof(item));
        ArgumentException.ThrowIfNullOrWhiteSpace(state, nameof(state));

        var dict = new Dictionary<string, string>(PersistentChanges, StringComparer.OrdinalIgnoreCase)
        {
            [item.Trim()] = state.Trim()
        };
        PersistentChanges = dict;
        SourceTurnId = turnId;
        SceneRevision = revision;
        Fingerprint = ComputeFingerprint(CharacterId, Location, CharacterState.Outfit, CharacterState.Hairstyle, CharacterState.Pose,
            CharacterState.Action, TimeOfDay, Weather, Lighting, Atmosphere, Props, PersistentChanges, SceneRevision);
        Version++;
    }

    public void Invalidate(Guid supersededByTurnId, int? supersededByRevision = null)
    {
        ValidUntilTurnId = supersededByTurnId;
        if (supersededByRevision.HasValue)
        {
            ValidUntilRevision = supersededByRevision.Value;
        }
        CharacterState.Invalidate(supersededByTurnId, supersededByRevision);
        Version++;
    }

    public bool IsActiveForRevision(int targetRevision)
    {
        if (targetRevision < ValidFromRevision) return false;
        if (ValidUntilRevision.HasValue && targetRevision >= ValidUntilRevision.Value) return false;
        return true;
    }
}
