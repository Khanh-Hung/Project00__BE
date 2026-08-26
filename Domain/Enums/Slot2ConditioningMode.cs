namespace Domain.Enums;

/// <summary>
/// Domain-level authority mode for Slot 2 (Previous Scene) conditioning.
/// Represents business intent for scene/style continuity without granting full anatomical conditioning authority.
/// </summary>
public enum Slot2ConditioningMode
{
    Bypassed = 0,
    SceneStyleContinuity = 1,
    FullLinearContinuity = 2
}
