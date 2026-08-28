namespace Domain.Enums;

public enum ImageJobStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    Queued = 5,
    Evaluating = 6,
    Quarantined = 7
}
