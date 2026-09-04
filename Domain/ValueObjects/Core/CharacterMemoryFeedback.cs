using System;

namespace Domain.ValueObjects;

public enum CharacterMemoryFeedbackType
{
    EventExperienced = 1,
    ActionCompleted = 2,
    ActionFailed = 3,
    NoActionTaken = 4
}

/// <summary>
/// Immutable value object representing a normalized memory feedback record generated after a cognitive cycle.
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
