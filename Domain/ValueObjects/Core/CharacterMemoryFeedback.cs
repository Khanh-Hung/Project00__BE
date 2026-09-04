using System;

namespace Domain.ValueObjects;

public enum CharacterMemoryFeedbackType
{
    NoActionTaken = 1,
    ActionFailed = 2,
    EventExperienced = 3,
    ActionCompleted = 4
}

/// <summary>
/// CharacterMemoryFeedback is an immutable result describing the persisted CharacterMemory created by the feedback operation.
/// It is not a separately persisted entity, and MemoryId identifies the resulting CharacterMemory.
/// Captures narrative outcome of the cycle without serializing raw internal state or execution graphs.
/// </summary>
public sealed record CharacterMemoryFeedback(
    Guid MemoryId,
    Guid CharacterId,
    Guid CycleId,
    Guid? EventId,
    Guid? ExecutionId,
    DateTimeOffset OccurredAtUtc,
    CharacterMemoryFeedbackType Type,
    string Content
);
