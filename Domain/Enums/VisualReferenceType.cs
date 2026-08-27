namespace Domain.Enums;

/// <summary>
/// Defines the role and authority of a visual reference in character identity and generation conditioning.
/// </summary>
public enum VisualReferenceType
{
    /// <summary>
    /// The primary authoritative visual reference defining canonical character appearance.
    /// </summary>
    Canonical = 0,

    /// <summary>
    /// Secondary authoritative reference supplementing canonical identity (e.g. alternate angle, expressions).
    /// </summary>
    SecondaryCanonical = 1,

    /// <summary>
    /// Transient scene-specific reference (e.g. environment, specific outfit/armor for a quest).
    /// </summary>
    SceneReference = 2,

    /// <summary>
    /// User-uploaded reference image awaiting or assigned reference status.
    /// </summary>
    UploadedReference = 3,

    /// <summary>
    /// Generated scene artifact recorded as visual evidence without automatic canonical identity promotion.
    /// </summary>
    GeneratedEvidence = 4
}
