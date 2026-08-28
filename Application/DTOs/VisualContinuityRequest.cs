using Domain.Entities;

namespace Application.DTOs;

/// <summary>
/// Encapsulates all domain inputs required for deterministic visual continuity resolution.
/// </summary>
public sealed record VisualContinuityRequest(
    SceneIntent Intent,
    SceneCompositionContext Context,
    int TargetRevision = 1
);
