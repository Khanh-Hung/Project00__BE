using System;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Status outcome of world event consumption.
/// </summary>
public enum WorldCognitiveEventConsumptionStatus
{
    Processed = 1,
    Duplicate = 2,
    Rejected = 3
}

/// <summary>
/// Result of consuming a WorldCognitiveEvent at the cognitive boundary.
/// </summary>
public sealed record WorldCognitiveEventConsumptionResult(
    Guid EventId,
    Guid CharacterId,
    Guid? CycleId,
    WorldCognitiveEventConsumptionStatus Status,
    string Message)
{
    public bool IsAccepted => Status == WorldCognitiveEventConsumptionStatus.Processed;
    public bool IsDuplicate => Status == WorldCognitiveEventConsumptionStatus.Duplicate;
    public bool IsRejected => Status == WorldCognitiveEventConsumptionStatus.Rejected;

    public static WorldCognitiveEventConsumptionResult ProcessedResult(Guid eventId, Guid characterId, Guid cycleId, string? message = null) =>
        new(eventId, characterId, cycleId, WorldCognitiveEventConsumptionStatus.Processed, message ?? "World event successfully processed by cognitive cycle.");

    public static WorldCognitiveEventConsumptionResult DuplicateResult(Guid eventId, Guid characterId, Guid? cycleId, string? message = null) =>
        new(eventId, characterId, cycleId, WorldCognitiveEventConsumptionStatus.Duplicate, message ?? "World event has already been consumed or is currently being processed.");

    public static WorldCognitiveEventConsumptionResult RejectedResult(Guid eventId, Guid characterId, string reason) =>
        new(eventId, characterId, null, WorldCognitiveEventConsumptionStatus.Rejected, reason);
}
