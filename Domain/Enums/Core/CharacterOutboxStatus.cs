namespace Domain.Enums;

/// <summary>
/// Lifecycle status of an outbox message representing a durable factual world or life simulation event.
/// </summary>
public enum CharacterOutboxStatus
{
    Pending = 0,
    Processing = 1,
    Published = 2,
    Failed = 3
}
