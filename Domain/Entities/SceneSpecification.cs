using System.Security.Cryptography;
using System.Text;
using Domain.Common;
using Domain.ValueObjects;
using Domain.ValueObjects.Scene;

namespace Domain.Entities;

/// <summary>
/// Authoritative domain aggregate representing a normalized, validated scene specification for image synthesis.
/// Captures what the scene contains (character, action, pose, location, environment, lighting, camera, mood)
/// rather than how a specific image model renders it.
/// Uses SceneFingerprint for deterministic content hashing.
/// </summary>
public sealed class SceneSpecification : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid? SessionId { get; private set; }
    public Guid? TurnId { get; private set; }
    public int SceneRevision { get; private set; } = 1;

    public string Location { get; private set; }
    public string Action { get; private set; }

    public string? Pose { get; private set; }
    public string? Lighting { get; private set; }
    public string? Camera { get; private set; }
    public string? Weather { get; private set; }
    public string? TimeOfDay { get; private set; }
    public string? Mood { get; private set; }
    public string? OutfitContext { get; private set; }

    public SceneEnvironment Environment { get; private set; }
    public string SceneFingerprint { get; private set; }

    private SceneSpecification()
    {
        Location = null!;
        Action = null!;
        Environment = null!;
        SceneFingerprint = null!;
    } // EF Core

    public SceneSpecification(
        Guid characterId,
        string location,
        string action,
        int sceneRevision = 1,
        Guid? sessionId = null,
        Guid? turnId = null,
        string? pose = null,
        SceneEnvironment? environment = null,
        string? lighting = null,
        string? camera = null,
        string? weather = null,
        string? timeOfDay = null,
        string? mood = null,
        string? outfitContext = null,
        DateTime? now = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(location))
            throw new ArgumentException("Location cannot be empty.", nameof(location));

        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("Action cannot be empty.", nameof(action));

        if (sceneRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(sceneRevision), "SceneRevision must be >= 1.");

        Id = Guid.CreateVersion7();
        CharacterId = characterId;
        Location = location.Trim();
        Action = action.Trim();
        SceneRevision = sceneRevision;
        SessionId = sessionId;
        TurnId = turnId;

        Pose = pose?.Trim();
        Lighting = lighting?.Trim();
        Camera = camera?.Trim();
        Weather = weather?.Trim();
        TimeOfDay = timeOfDay?.Trim();
        Mood = mood?.Trim();
        OutfitContext = outfitContext?.Trim();

        Environment = environment ?? new SceneEnvironment(
            location: Location,
            weather: Weather,
            timeOfDay: TimeOfDay,
            lighting: Lighting,
            atmosphere: Mood
        );

        SceneFingerprint = ComputeFingerprint(
            CharacterId, Location, Action, Pose, Lighting, Camera, Weather, TimeOfDay, Mood, OutfitContext, Environment
        );

        CreatedAt = now ?? DateTime.UtcNow;
    }

    public static string ComputeFingerprint(
        Guid characterId,
        string location,
        string action,
        string? pose,
        string? lighting,
        string? camera,
        string? weather,
        string? timeOfDay,
        string? mood,
        string? outfitContext,
        SceneEnvironment? environment)
    {
        var raw = new StringBuilder();
        raw.Append(characterId).Append('|');
        raw.Append(location?.Trim().ToLowerInvariant()).Append('|');
        raw.Append(action?.Trim().ToLowerInvariant()).Append('|');
        raw.Append(pose?.Trim().ToLowerInvariant() ?? "").Append('|');
        raw.Append(lighting?.Trim().ToLowerInvariant() ?? "").Append('|');
        raw.Append(camera?.Trim().ToLowerInvariant() ?? "").Append('|');
        raw.Append(weather?.Trim().ToLowerInvariant() ?? "").Append('|');
        raw.Append(timeOfDay?.Trim().ToLowerInvariant() ?? "").Append('|');
        raw.Append(mood?.Trim().ToLowerInvariant() ?? "").Append('|');
        raw.Append(outfitContext?.Trim().ToLowerInvariant() ?? "").Append('|');

        if (environment != null)
        {
            raw.Append(environment.Architecture?.Trim().ToLowerInvariant() ?? "").Append('|');
            raw.Append(string.Join(",", environment.BackgroundElements.Select(e => e.Trim().ToLowerInvariant()))).Append('|');
            raw.Append(string.Join(",", environment.ForegroundElements.Select(e => e.Trim().ToLowerInvariant()))).Append('|');
            raw.Append(string.Join(",", environment.Props.Select(e => e.Trim().ToLowerInvariant())));
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToString()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
