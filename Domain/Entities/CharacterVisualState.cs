using System.Text.Json.Serialization;

namespace Domain.Entities;

/// <summary>
/// Authoritative domain aggregate capturing the mutable visual state of a Character within a scene.
/// Does NOT duplicate immutable core identity traits (eye color, facial structure) from PR30 CharacterVisualProfile.
/// Supports temporal validity and evolution tracking across monotonic scene revisions.
/// </summary>
public sealed class CharacterVisualState
{
    public Guid Id { get; private set; }
    public Guid CharacterId { get; private set; }
    public int SceneRevision { get; private set; } = 1;
    public string Location { get; private set; }
    public string? Outfit { get; private set; }
    public string? Hairstyle { get; private set; }
    public IReadOnlyList<string> AppearanceOverrides { get; private set; } = Array.Empty<string>();
    public string? Pose { get; private set; }
    public string? Action { get; private set; }
    public IReadOnlyList<string> ActiveProps { get; private set; } = Array.Empty<string>();

    public Guid? ValidFromTurnId { get; private set; }
    public Guid? ValidUntilTurnId { get; private set; }
    public Guid? SourceTurnId { get; private set; }
    public int ValidFromRevision { get; private set; } = 1;
    public int? ValidUntilRevision { get; private set; }
    public float Confidence { get; private set; } = 1.0f;
    public uint Version { get; private set; } = 1;
    public DateTime CreatedAt { get; private set; }

    [JsonConstructor]
    public CharacterVisualState(
        Guid characterId,
        string location,
        int sceneRevision = 1,
        string? outfit = null,
        string? hairstyle = null,
        IReadOnlyList<string>? appearanceOverrides = null,
        string? pose = null,
        string? action = null,
        IReadOnlyList<string>? activeProps = null,
        Guid? validFromTurnId = null,
        Guid? validUntilTurnId = null,
        Guid? sourceTurnId = null,
        int validFromRevision = 1,
        int? validUntilRevision = null,
        float confidence = 1.0f,
        uint version = 1,
        Guid? id = null,
        DateTime? createdAt = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(location))
            throw new ArgumentException("Location cannot be empty.", nameof(location));

        if (sceneRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(sceneRevision), "SceneRevision must be >= 1.");

        if (confidence < 0.0f || confidence > 1.0f)
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between 0.0 and 1.0.");

        Id = id ?? Guid.CreateVersion7();
        CharacterId = characterId;
        Location = location.Trim();
        SceneRevision = sceneRevision;
        Outfit = outfit?.Trim();
        Hairstyle = hairstyle?.Trim();
        AppearanceOverrides = appearanceOverrides?.Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList() 
                              ?? (IReadOnlyList<string>)Array.Empty<string>();
        Pose = pose?.Trim();
        Action = action?.Trim();
        ActiveProps = activeProps?.Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList() 
                      ?? (IReadOnlyList<string>)Array.Empty<string>();
        ValidFromTurnId = validFromTurnId;
        ValidUntilTurnId = validUntilTurnId;
        SourceTurnId = sourceTurnId ?? validFromTurnId;
        ValidFromRevision = validFromRevision > 0 ? validFromRevision : sceneRevision;
        ValidUntilRevision = validUntilRevision;
        Confidence = confidence;
        Version = version;
        CreatedAt = createdAt ?? DateTime.UtcNow;
    }

    public void EvolveOutfit(string newOutfit, Guid turnId, int revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newOutfit, nameof(newOutfit));
        Outfit = newOutfit.Trim();
        SourceTurnId = turnId;
        SceneRevision = revision;
        Version++;
    }

    public void EvolveHairstyle(string newHairstyle, Guid turnId, int revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newHairstyle, nameof(newHairstyle));
        Hairstyle = newHairstyle.Trim();
        SourceTurnId = turnId;
        SceneRevision = revision;
        Version++;
    }

    public void EvolvePoseAndAction(string? pose, string? action, Guid turnId, int revision)
    {
        if (!string.IsNullOrWhiteSpace(pose)) Pose = pose.Trim();
        if (!string.IsNullOrWhiteSpace(action)) Action = action.Trim();
        SourceTurnId = turnId;
        SceneRevision = revision;
        Version++;
    }

    public void SetActiveProps(IEnumerable<string> props, Guid turnId)
    {
        ActiveProps = props.Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
        SourceTurnId = turnId;
        Version++;
    }

    public void Invalidate(Guid supersededByTurnId, int? supersededByRevision = null)
    {
        ValidUntilTurnId = supersededByTurnId;
        if (supersededByRevision.HasValue)
        {
            ValidUntilRevision = supersededByRevision.Value;
        }
        Version++;
    }

    public bool IsActiveForRevision(int targetRevision)
    {
        if (targetRevision < ValidFromRevision) return false;
        if (ValidUntilRevision.HasValue && targetRevision >= ValidUntilRevision.Value) return false;
        return true;
    }
}
