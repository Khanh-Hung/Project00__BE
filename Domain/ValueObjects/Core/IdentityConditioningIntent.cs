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
    /// Checks whether a canonical character appearance reference is present.
    /// </summary>
    public bool HasCanonicalReference => !string.IsNullOrWhiteSpace(CanonicalReferenceUrl);

    /// <summary>
    /// Checks whether a predecessor scene appearance reference is present.
    /// </summary>
    public bool HasPreviousSceneReference => !string.IsNullOrWhiteSpace(PreviousSceneReferenceUrl);

    /// <summary>
    /// Checks whether any visual reference image (canonical or predecessor scene) is present.
    /// </summary>
    public bool HasReferences => HasCanonicalReference || HasPreviousSceneReference;

    /// <summary>
    /// Creates an identity conditioning intent from reference URLs and optional continuity context.
    /// Consistent semantic: If either canonical reference or previous scene reference exists, conditioning is active/required.
    /// </summary>
    public static IdentityConditioningIntent FromReferences(
        string? canonicalReferenceUrl,
        string? previousSceneReferenceUrl = null,
        float? preservationStrength = null,
        Slot2Context context = Slot2Context.ColdStart,
        Slot2ConditioningMode continuityMode = Slot2ConditioningMode.SceneStyleContinuity)
    {
        var hasCanonical = !string.IsNullOrWhiteSpace(canonicalReferenceUrl);
        var hasPrevious = !string.IsNullOrWhiteSpace(previousSceneReferenceUrl);
        var isRequired = hasCanonical || hasPrevious;

        return new IdentityConditioningIntent(
            IsRequired: isRequired,
            CanonicalReferenceUrl: hasCanonical ? canonicalReferenceUrl : null,
            PreviousSceneReferenceUrl: hasPrevious ? previousSceneReferenceUrl : null,
            PreservationStrength: preservationStrength,
            Context: context,
            ContinuityMode: continuityMode
        );
    }
}
