using System;
using System.Collections.Generic;
using Domain.Entities;
using Domain.Enums;

namespace Application.Contracts.LifeSimulation;

/// <summary>
/// Domain event representing an activity lifecycle occurrence within the simulation.
/// Note on Event Boundary: In PR50 (Foundation), LifeSimulationEvent represents an in-memory
/// factual occurrence produced during the tick. In PR51+, these events are staged into the
/// Transactional Outbox and persisted as WorldCognitiveEvents for asynchronous ingestion by the cognitive cycle.
/// </summary>
public sealed record LifeSimulationEvent(
    Guid EventId,
    Guid CharacterId,
    DateTimeOffset OccurredAtUtc,
    Guid ActivityId,
    LifeActivityType ActivityType,
    string EventType,
    string Description
);

/// <summary>
/// Result of executing a simulation tick for a character.
/// </summary>
public sealed record LifeSimulationTickResult(
    Guid SimulationTickId,
    Guid CharacterId,
    DateTimeOffset SimulationTimeUtc,
    CharacterLifeActivity? ActiveActivity,
    IReadOnlyList<CharacterLifeActivity> CompletedActivities,
    IReadOnlyList<CharacterLifeActivity> StartedActivities,
    IReadOnlyList<LifeSimulationEvent> Events,
    bool IsSuccess = true,
    string? Message = null
);

/// <summary>
/// Thrown when attempting to schedule an activity that overlaps with an existing activity.
/// </summary>
public class LifeActivityScheduleConflictException : InvalidOperationException
{
    public Guid CharacterId { get; }
    public DateTime StartAtUtc { get; }
    public DateTime PlannedEndAtUtc { get; }
    public Guid? ConflictingActivityId { get; }

    public LifeActivityScheduleConflictException(
        Guid characterId,
        DateTime startAtUtc,
        DateTime plannedEndAtUtc,
        Guid? conflictingActivityId = null,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"Activity for character {characterId} from {startAtUtc:O} to {plannedEndAtUtc:O} overlaps with existing activity {conflictingActivityId}.", innerException)
    {
        CharacterId = characterId;
        StartAtUtc = startAtUtc;
        PlannedEndAtUtc = plannedEndAtUtc;
        ConflictingActivityId = conflictingActivityId;
    }
}

/// <summary>
/// Thrown when an invalid activity lifecycle transition is attempted.
/// </summary>
public class LifeActivityInvalidTransitionException : InvalidOperationException
{
    public Guid ActivityId { get; }
    public LifeActivityStatus CurrentStatus { get; }
    public LifeActivityStatus TargetStatus { get; }

    public LifeActivityInvalidTransitionException(
        Guid activityId,
        LifeActivityStatus currentStatus,
        LifeActivityStatus targetStatus,
        string? message = null)
        : base(message ?? $"Cannot transition activity {activityId} from {currentStatus} to {targetStatus}.")
    {
        ActivityId = activityId;
        CurrentStatus = currentStatus;
        TargetStatus = targetStatus;
    }
}

/// <summary>
/// Thrown when an optimistic concurrency conflict occurs during life simulation state updates.
/// </summary>
public class LifeSimulationConcurrencyException : Exception
{
    public Guid CharacterId { get; }
    public Guid? ActivityId { get; }

    public LifeSimulationConcurrencyException(Guid characterId, Guid? activityId = null, string? message = null, Exception? innerException = null)
        : base(message ?? $"Concurrency conflict occurred while updating life simulation state for character {characterId} (Activity: {activityId}).", innerException)
    {
        CharacterId = characterId;
        ActivityId = activityId;
    }
}
