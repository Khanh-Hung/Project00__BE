using Domain.Common;

namespace Domain.Entities;

/// <summary>
/// Immutable visual evidence ledger capturing generated and validated images of a Character across scene revisions.
/// Preserves historical lineage for visual memory and style continuity.
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

        if (visualProfileVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(visualProfileVersion), "VisualProfileVersion must be >= 1.");

        if (sceneRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(sceneRevision), "SceneRevision must be >= 1.");

        if (artifactId == Guid.Empty)
            throw new ArgumentException("ArtifactId cannot be empty.", nameof(artifactId));

        if (qualityScore.HasValue && (qualityScore.Value < 0.0f || qualityScore.Value > 1.0f))
            throw new ArgumentOutOfRangeException(nameof(qualityScore), "QualityScore must be between 0.0 and 1.0.");

        if (identityScore.HasValue && (identityScore.Value < 0.0f || identityScore.Value > 1.0f))
            throw new ArgumentOutOfRangeException(nameof(identityScore), "IdentityScore must be between 0.0 and 1.0.");

        if (featureScore.HasValue && (featureScore.Value < 0.0f || featureScore.Value > 1.0f))
            throw new ArgumentOutOfRangeException(nameof(featureScore), "FeatureScore must be between 0.0 and 1.0.");

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
