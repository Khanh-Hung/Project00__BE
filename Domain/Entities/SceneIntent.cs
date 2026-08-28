namespace Domain.Entities;

/// <summary>
/// Raw unnormalized scene generation intent extracted from chat dialogue, character turns, or user commands.
/// Decoupled from normalized SceneSpecification.
/// Supports explicit outfit, hairstyle, pose, action, and environment hints.
/// </summary>
public sealed class SceneIntent
{
    public Guid Id { get; private set; }
    public Guid CharacterId { get; private set; }
    public Guid? SessionId { get; private set; }
    public Guid? TurnId { get; private set; }

    public string LocationHint { get; private set; }
    public string ActionHint { get; private set; }

    public string? PoseHint { get; private set; }
    public string? EnvironmentHint { get; private set; }
    public string? LightingHint { get; private set; }
    public string? CameraHint { get; private set; }
    public string? WeatherHint { get; private set; }
    public string? TimeOfDayHint { get; private set; }
    public string? MoodHint { get; private set; }
    public string? OutfitHint { get; private set; }
    public string? HairstyleHint { get; private set; }

    public IReadOnlyList<string> ObjectHints { get; private set; }
    public IReadOnlyList<string> AtmosphereHints { get; private set; }

    public SceneIntent(
        Guid characterId,
        string locationHint,
        string actionHint,
        Guid? sessionId = null,
        Guid? turnId = null,
        string? poseHint = null,
        string? environmentHint = null,
        string? lightingHint = null,
        string? cameraHint = null,
        string? weatherHint = null,
        string? timeOfDayHint = null,
        string? moodHint = null,
        string? outfitHint = null,
        string? hairstyleHint = null,
        IEnumerable<string>? objectHints = null,
        IEnumerable<string>? atmosphereHints = null,
        Guid? id = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(locationHint))
            throw new ArgumentException("LocationHint cannot be empty.", nameof(locationHint));

        if (string.IsNullOrWhiteSpace(actionHint))
            throw new ArgumentException("ActionHint cannot be empty.", nameof(actionHint));

        Id = id ?? Guid.CreateVersion7();
        CharacterId = characterId;
        LocationHint = locationHint.Trim();
        ActionHint = actionHint.Trim();
        SessionId = sessionId;
        TurnId = turnId;
        PoseHint = poseHint?.Trim();
        EnvironmentHint = environmentHint?.Trim();
        LightingHint = lightingHint?.Trim();
        CameraHint = cameraHint?.Trim();
        WeatherHint = weatherHint?.Trim();
        TimeOfDayHint = timeOfDayHint?.Trim();
        MoodHint = moodHint?.Trim();
        OutfitHint = outfitHint?.Trim();
        HairstyleHint = hairstyleHint?.Trim();
        ObjectHints = objectHints?.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList() ?? new List<string>();
        AtmosphereHints = atmosphereHints?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() ?? new List<string>();
    }
}
