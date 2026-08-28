namespace Domain.Enums;

/// <summary>
/// Lifecycle execution status of an autonomous character tick.
/// </summary>
public enum AutonomyTickStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
