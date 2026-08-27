using Domain.Common;

namespace Domain.Entities;

/// <summary>
/// Authoritative, persistent visual identity profile of a character.
/// Separates immutable identity traits from mutable appearance traits and guarantees strictly monotonic VisualVersion evolution.
/// Protected by optimistic concurrency tokens (VisualVersion).
/// </summary>
public sealed class CharacterVisualProfile : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public int VisualVersion { get; private set; } = 1;

    public Guid? PrimaryReferenceId { get; private set; }
    public Guid? FaceReferenceId { get; private set; }

    // Immutable Identity Traits (Core visual invariants)
    public string? HairDescription { get; private set; }
    public string? EyeDescription { get; private set; }
    public string? SkinDescription { get; private set; }
    public string? BodyDescription { get; private set; }
    public string? DistinguishingFeatures { get; private set; }

    private CharacterVisualProfile() { } // EF Core

    public CharacterVisualProfile(
        Guid characterId,
        string? hairDescription = null,
        string? eyeDescription = null,
        string? skinDescription = null,
        string? bodyDescription = null,
        string? distinguishingFeatures = null,
        Guid? primaryReferenceId = null,
        Guid? faceReferenceId = null,
        int visualVersion = 1,
        DateTime? now = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        Id = Guid.CreateVersion7();
        CharacterId = characterId;
        VisualVersion = Math.Max(1, visualVersion);
        HairDescription = hairDescription;
        EyeDescription = eyeDescription;
        SkinDescription = skinDescription;
        BodyDescription = bodyDescription;
        DistinguishingFeatures = distinguishingFeatures;
        PrimaryReferenceId = primaryReferenceId;
        FaceReferenceId = faceReferenceId;
        CreatedAt = now ?? DateTime.UtcNow;
        UpdatedAt = now ?? DateTime.UtcNow;
    }

    /// <summary>
    /// Explicitly updates mutable or refined visual appearance traits and advances the visual version monotonically.
    /// </summary>
    public void UpdateAppearance(
        string? hairDescription,
        string? eyeDescription,
        string? skinDescription,
        string? bodyDescription,
        string? distinguishingFeatures,
        DateTime now)
    {
        HairDescription = hairDescription;
        EyeDescription = eyeDescription;
        SkinDescription = skinDescription;
        BodyDescription = bodyDescription;
        DistinguishingFeatures = distinguishingFeatures;
        VisualVersion++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Promotes an authoritative reference to canonical identity and advances the visual version monotonically.
    /// </summary>
    public void PromoteReferenceToCanonical(Guid referenceId, bool isFaceOnly, DateTime now)
    {
        if (referenceId == Guid.Empty)
            throw new ArgumentException("ReferenceId cannot be empty.", nameof(referenceId));

        if (isFaceOnly)
        {
            FaceReferenceId = referenceId;
        }
        else
        {
            PrimaryReferenceId = referenceId;
            FaceReferenceId ??= referenceId;
        }

        VisualVersion++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Explicitly sets the primary reference pointer.
    /// </summary>
    public void SetPrimaryReference(Guid referenceId, DateTime now)
    {
        if (referenceId == Guid.Empty)
            throw new ArgumentException("ReferenceId cannot be empty.", nameof(referenceId));

        PrimaryReferenceId = referenceId;
        VisualVersion++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Explicitly sets the face reference pointer.
    /// </summary>
    public void SetFaceReference(Guid referenceId, DateTime now)
    {
        if (referenceId == Guid.Empty)
            throw new ArgumentException("ReferenceId cannot be empty.", nameof(referenceId));

        FaceReferenceId = referenceId;
        VisualVersion++;
        UpdatedAt = now;
    }
}
