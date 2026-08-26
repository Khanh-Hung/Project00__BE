namespace Domain.Enums;

/// <summary>
/// Domain-level authority mode for Slot 2 (Previous Scene) conditioning.
/// Distinguishes between pure scene/style continuity (Down/Mid blocks only) vs full linear continuity vs complete bypass.
/// </summary>
public enum Slot2ConditioningMode
{
    Bypassed = 0,
    SceneStyleContinuity = 1,
    FullLinearContinuity = 2
}
