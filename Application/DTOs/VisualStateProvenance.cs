using Domain.Enums;

namespace Application.DTOs;

/// <summary>
/// Comprehensive provenance record detailing the exact authority and lineage source for each component
/// of the resolved SceneVisualState. Enables end-to-end auditability and debugging.
/// </summary>
public sealed record VisualStateProvenance(
    string OutfitSource,
    string HairstyleSource,
    string PoseSource,
    string ActionSource,
    string LocationSource,
    string WeatherSource,
    string TimeOfDaySource,
    string LightingSource,
    string AtmosphereSource,
    string PropsSource,
    string WorldMutationsSource,
    SceneTransitionType TransitionType,
    DateTime ResolvedAt
);
