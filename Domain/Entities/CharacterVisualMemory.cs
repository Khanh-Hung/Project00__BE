using Domain.Common;

namespace Domain.Entities;

/// <summary>
/// Persistent visual memory / visual evidence recorded from generation workflows.
/// Maintains historical traceability: what the character has looked like across scenes and profile versions.
/// </summary>
public sealed class CharacterVisualMemory : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public int VisualProfileVersion { get; private set; }
    public int SceneRevision { get; private set; }
    public Guid ArtifactId { get; private set; }

    public string? Context { get; private set; }
    public string? Tags { get; private set; }
    public float? QualityScore { get; private set; }
    public float? IdentityScore { get; private set; }
    public float? FeatureScore { get; private set; }

    private CharacterVisualMemory() { } // EF Core

    public CharacterVisualMemory(
        Guid characterId,
        int visualProfileVersion,
        int sceneRevision,
        Guid artifactId,
        string? context = null,
        string? tags = null,
        float? qualityScore = null,
        float? identityScore = null,
        float? featureScore = null,
        DateTime? now = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (artifactId == Guid.Empty)
            throw new ArgumentException("ArtifactId cannot be empty.", nameof(artifactId));

        Id = Guid.CreateVersion7();
        CharacterId = characterId;
        VisualProfileVersion = visualProfileVersion;
        SceneRevision = sceneRevision;
        ArtifactId = artifactId;
        Context = context;
        Tags = tags;
        QualityScore = qualityScore;
        IdentityScore = identityScore;
        FeatureScore = featureScore;
        CreatedAt = now ?? DateTime.UtcNow;
    }
}
