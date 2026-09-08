namespace Domain.Enums;

/// <summary>
/// Lifecycle state of a world cognitive event consumption claim.
/// </summary>
public enum EventConsumptionState
{
    InProgress = 1,
    Consumed = 2,
    Failed = 3
}
