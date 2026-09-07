namespace Domain.Enums;

/// <summary>
/// Lifecycle status of a character life activity.
/// Valid transitions:
/// Scheduled -> Active -> Completed
/// Scheduled -> Cancelled
/// Active -> Cancelled
/// </summary>
public enum LifeActivityStatus
{
    Scheduled = 1,
    Active = 2,
    Completed = 3,
    Cancelled = 4
}
