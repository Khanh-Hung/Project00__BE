using Domain.Entities;
using Domain.Enums;

namespace Application.DTOs;

/// <summary>
/// Authoritative result of the VisualContinuityResolver containing the normalized SceneVisualState,
/// transition classification, delta tracking (changed, preserved, invalidated fields), provenance, and deterministic fingerprint.
/// </summary>
public sealed record VisualContinuityResult(
    SceneVisualState SceneVisualState,
    SceneTransitionType TransitionType,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<string> PreservedFields,
    IReadOnlyList<string> InvalidatedFields,
    VisualStateProvenance Provenance,
    int SceneRevision,
    string Fingerprint
);
