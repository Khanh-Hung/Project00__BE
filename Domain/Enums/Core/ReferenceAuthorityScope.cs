namespace Domain.Enums;

/// <summary>
/// Domain-level authority scope distinguishing between canonical identity conditioning (Slot 1)
/// and recursive scene continuity conditioning (Slot 2).
/// </summary>
public enum ReferenceAuthorityScope
{
    /// <summary>
    /// Exclusive authority over biological anatomy, sexual dimorphism, facial micro-features, hair, eyes, and critical signature features.
    /// </summary>
    CanonicalIdentity = 1,

    /// <summary>
    /// Limited authority over lighting, color palette, atmospheric tone, and environmental composition continuity.
    /// </summary>
    SceneContinuity = 2
}
