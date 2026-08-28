using Domain.Common;

namespace Domain.Entities;

/// <summary>
/// Authoritative, persistent visual profile of a character.
/// Strictly separates Core Immutable Identity Traits from Mutable Appearance Traits.
/// Changes to appearance or explicit core identity refinements advance VisualVersion monotonically.
/// Protected by optimistic concurrency tokens (VisualVersion).
/// </summary>
public sealed class CharacterVisualProfile : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public int VisualVersion { get; private set; } = 1;

    public Guid? PrimaryReferenceId { get; private set; }
    public Guid? FaceReferenceId { get; private set; }

    // --- Core Immutable Identity Traits (Permanent character invariants) ---
    public string? EyeColor { get; private set; }
    public string? HairColor { get; private set; }
    public string? SkinTone { get; private set; }
    public string? FacialFeatures { get; private set; }
    public string? PermanentMarks { get; private set; }
    public string? BodyIdentity { get; private set; }

    // --- Mutable Appearance Traits (Styling, outfits, temporary scene presentation) ---
    public string? Hairstyle { get; private set; }
    public string? CurrentOutfit { get; private set; }
    public string? Makeup { get; private set; }
    public string? Accessories { get; private set; }
    public string? TemporaryAppearance { get; private set; }

    private CharacterVisualProfile() { } // EF Core

    public CharacterVisualProfile(
        Guid characterId,
        string? eyeColor = null,
        string? hairColor = null,
        string? skinTone = null,
        string? facialFeatures = null,
        string? permanentMarks = null,
        string? bodyIdentity = null,
        string? hairstyle = null,
        string? currentOutfit = null,
        string? makeup = null,
        string? accessories = null,
        string? temporaryAppearance = null,
        int visualVersion = 1,
        DateTime? now = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        Id = Guid.CreateVersion7();
        CharacterId = characterId;
        VisualVersion = Math.Max(1, visualVersion);

        // Core Identity
        EyeColor = eyeColor;
        HairColor = hairColor;
        SkinTone = skinTone;
        FacialFeatures = facialFeatures;
        PermanentMarks = permanentMarks;
        BodyIdentity = bodyIdentity;

        // Mutable Appearance
        Hairstyle = hairstyle;
        CurrentOutfit = currentOutfit;
        Makeup = makeup;
        Accessories = accessories;
        TemporaryAppearance = temporaryAppearance;

        CreatedAt = now ?? DateTime.UtcNow;
        UpdatedAt = now ?? DateTime.UtcNow;
    }

    /// <summary>
    /// Explicit domain operation to update mutable appearance/styling traits.
    /// Does not alter core immutable identity traits. Advances VisualVersion monotonically.
    /// </summary>
    public void UpdateAppearance(
        string? hairstyle,
        string? currentOutfit,
        string? makeup,
        string? accessories,
        string? temporaryAppearance,
        DateTime now)
    {
        Hairstyle = hairstyle;
        CurrentOutfit = currentOutfit;
        Makeup = makeup;
        Accessories = accessories;
        TemporaryAppearance = temporaryAppearance;
        VisualVersion++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Explicit domain operation for authorized core identity refinement (e.g. character evolution or lore expansion).
    /// Advances VisualVersion monotonically.
    /// </summary>
    public void RefineCoreIdentity(
        string? eyeColor,
        string? hairColor,
        string? skinTone,
        string? facialFeatures,
        string? permanentMarks,
        string? bodyIdentity,
        DateTime now)
    {
        EyeColor = eyeColor;
        HairColor = hairColor;
        SkinTone = skinTone;
        FacialFeatures = facialFeatures;
        PermanentMarks = permanentMarks;
        BodyIdentity = bodyIdentity;
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
