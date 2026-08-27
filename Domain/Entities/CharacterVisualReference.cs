using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Persistent visual reference entity associated with a Character and its Visual Profile.
/// Can be canonical, secondary, scene-specific, or uploaded.
/// </summary>
public sealed class CharacterVisualReference : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid? VisualProfileId { get; private set; }

    public Guid? ArtifactId { get; private set; }
    public string ReferenceUrl { get; private set; } = string.Empty;

    public VisualReferenceType Type { get; private set; }
    public VisualReferenceStatus Status { get; private set; }
    public bool IsCanonical { get; private set; }
    public int Priority { get; private set; }

    public Guid? SourceGenerationJobId { get; private set; }
    public int SourceVisualRevision { get; private set; }

    public DateTime? PromotedAt { get; private set; }
    public DateTime? ArchivedAt { get; private set; }

    private CharacterVisualReference() { } // EF Core

    public CharacterVisualReference(
        Guid characterId,
        string referenceUrl,
        VisualReferenceType type = VisualReferenceType.SecondaryCanonical,
        VisualReferenceStatus status = VisualReferenceStatus.Active,
        bool isCanonical = false,
        Guid? visualProfileId = null,
        Guid? artifactId = null,
        int priority = 0,
        Guid? sourceGenerationJobId = null,
        int sourceVisualRevision = 0,
        DateTime? now = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(referenceUrl))
            throw new ArgumentException("ReferenceUrl cannot be empty.", nameof(referenceUrl));

        Id = Guid.CreateVersion7();
        CharacterId = characterId;
        ReferenceUrl = referenceUrl;
        Type = type;
        Status = status;
        IsCanonical = isCanonical;
        VisualProfileId = visualProfileId;
        ArtifactId = artifactId;
        Priority = priority;
        SourceGenerationJobId = sourceGenerationJobId;
        SourceVisualRevision = sourceVisualRevision;
        CreatedAt = now ?? DateTime.UtcNow;
        PromotedAt = isCanonical ? (now ?? DateTime.UtcNow) : null;
    }

    /// <summary>
    /// Explicit domain operation to promote this reference to primary canonical identity.
    /// Fails if the reference is archived.
    /// </summary>
    public void PromoteToCanonical(DateTime now)
    {
        if (Status == VisualReferenceStatus.Archived)
        {
            throw new InvalidOperationException($"Cannot promote visual reference '{Id}' because it is archived.");
        }

        IsCanonical = true;
        Type = VisualReferenceType.Canonical;
        Status = VisualReferenceStatus.Active;
        PromotedAt = now;
    }

    /// <summary>
    /// Demotes a canonical reference to secondary canonical status when superseded.
    /// </summary>
    public void DemoteCanonical(DateTime now)
    {
        IsCanonical = false;
        Type = VisualReferenceType.SecondaryCanonical;
    }

    /// <summary>
    /// Archives this reference, removing it from active reference selection while preserving audit lineage.
    /// </summary>
    public void Archive(DateTime now)
    {
        Status = VisualReferenceStatus.Archived;
        IsCanonical = false;
        ArchivedAt = now;
    }
}
