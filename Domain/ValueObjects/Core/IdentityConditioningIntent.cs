using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Model-agnostic intent for character visual identity and continuity conditioning.
/// Represents WHAT identity preservation is required, independent of generation models,
/// conditioning mechanisms (IP-Adapter, CLIP Vision, PuLID, etc.), or workflow node graphs.
/// </summary>
public sealed record IdentityConditioningIntent(
    bool IsRequired,
    string? CanonicalReferenceUrl = null,
    string? PreviousSceneReferenceUrl = null,
    float? PreservationStrength = null,
    Slot2Context Context = Slot2Context.ColdStart,
    Slot2ConditioningMode ContinuityMode = Slot2ConditioningMode.SceneStyleContinuity
)
{
    /// <summary>
    /// Represents no identity conditioning requirement (e.g. pure text-to-image).
    /// </summary>
    public static IdentityConditioningIntent None => new(IsRequired: false);

    /// <summary>
    /// Checks whether any visual reference image (canonical or predecessor scene) is present.
    /// </summary>
    public bool HasReferences => !string.IsNullOrWhiteSpace(CanonicalReferenceUrl) || !string.IsNullOrWhiteSpace(PreviousSceneReferenceUrl);

    /// <summary>
    /// Creates an identity conditioning intent from reference URLs and optional continuity context.
    /// </summary>
    public static IdentityConditioningIntent FromReferences(
        string? canonicalReferenceUrl,
        string? previousSceneReferenceUrl = null,
        float? preservationStrength = null,
        Slot2Context context = Slot2Context.ColdStart,
        Slot2ConditioningMode continuityMode = Slot2ConditioningMode.SceneStyleContinuity)
    {
        var isRequired = !string.IsNullOrWhiteSpace(canonicalReferenceUrl);
        return new IdentityConditioningIntent(
            IsRequired: isRequired,
            CanonicalReferenceUrl: isRequired ? canonicalReferenceUrl : null,
            PreviousSceneReferenceUrl: previousSceneReferenceUrl,
            PreservationStrength: preservationStrength,
            Context: context,
            ContinuityMode: continuityMode
        );
    }
}
