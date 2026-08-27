using Domain.Common;
using Domain.ValueObjects.Scene;

namespace Domain.Entities;

/// <summary>
/// Authoritative domain aggregate representing a normalized, validated scene specification for image synthesis.
/// Captures what the scene contains (character, action, pose, location, environment, lighting, camera, mood)
/// rather than how a specific image model renders it.
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
    public string? Environment { get; private set; }
    public string? Lighting { get; private set; }
    public string? Camera { get; private set; }
    public string? Weather { get; private set; }
    public string? TimeOfDay { get; private set; }
    public string? Mood { get; private set; }
    public string? OutfitContext { get; private set; }

    public IReadOnlyList<string> Objects { get; private set; } = new List<string>();
    public IReadOnlyList<string> AtmosphereElements { get; private set; } = new List<string>();

    private SceneSpecification()
    {
        Location = null!;
        Action = null!;
    } // EF Core

    public SceneSpecification(
        Guid characterId,
        string location,
        string action,
        int sceneRevision = 1,
        Guid? sessionId = null,
        Guid? turnId = null,
        string? pose = null,
        string? environment = null,
        string? lighting = null,
        string? camera = null,
        string? weather = null,
        string? timeOfDay = null,
        string? mood = null,
        string? outfitContext = null,
        IEnumerable<string>? objects = null,
        IEnumerable<string>? atmosphereElements = null,
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
        Environment = environment?.Trim();
        Lighting = lighting?.Trim();
        Camera = camera?.Trim();
        Weather = weather?.Trim();
        TimeOfDay = timeOfDay?.Trim();
        Mood = mood?.Trim();
        OutfitContext = outfitContext?.Trim();

        Objects = objects?.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList() ?? new List<string>();
        AtmosphereElements = atmosphereElements?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() ?? new List<string>();

        CreatedAt = now ?? DateTime.UtcNow;
    }
}
