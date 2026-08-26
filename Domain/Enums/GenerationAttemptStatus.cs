namespace Domain.Enums;

public enum GenerationAttemptStatus
{
    Running = 0,
    Succeeded = 1,
    Degraded = 2,
    Failed = 3,
    Pending = 4,
    Evaluating = 5,
    Quarantined = 6
}
