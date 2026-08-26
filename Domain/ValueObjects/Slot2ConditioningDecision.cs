using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Authoritative runtime decision determining whether Slot 2 (Previous Scene) conditioning is active
/// and specifying its exact weight, end_at timestep, and semantic context.
/// </summary>
public sealed record Slot2ConditioningDecision(
    bool IsActive,
    float Weight,
    float EndAt,
    Slot2Context Context,
    Slot2ConditioningMode Mode = Slot2ConditioningMode.SceneStyleContinuity
);
