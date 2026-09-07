using System;
using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Authoritative persistent domain aggregate representing a scheduled or active life activity
/// of a character within the life simulation subsystem.
/// Strictly decoupled from runtime physiological/psychological needs state (CharacterState).
/// Optimistic concurrency token (Version) prevents lost updates across concurrent workers.
/// </summary>
public sealed class CharacterLifeActivity
{
    public Guid Id { get; private set; }
    public Guid CharacterId { get; private set; }
    public LifeActivityType ActivityType { get; private set; }
    public LifeActivityStatus Status { get; private set; }
    public DateTime StartAtUtc { get; private set; }
    public DateTime PlannedEndAtUtc { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public string? CancellationReason { get; private set; }
    public string? Metadata { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public uint Version { get; private set; } = 1;

    private CharacterLifeActivity() { } // EF Core

    public CharacterLifeActivity(
        Guid characterId,
        LifeActivityType activityType,
        DateTime startAtUtc,
        DateTime plannedEndAtUtc,
        LifeActivityStatus status = LifeActivityStatus.Scheduled,
        string? metadata = null,
        DateTime? startedAtUtc = null,
        DateTime? completedAtUtc = null,
        string? cancellationReason = null,
        DateTime? createdAtUtc = null,
        uint version = 1,
        Guid? id = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (startAtUtc >= plannedEndAtUtc)
            throw new ArgumentException($"StartAtUtc ({startAtUtc:O}) must be strictly before PlannedEndAtUtc ({plannedEndAtUtc:O}).", nameof(startAtUtc));

        Id = id ?? Guid.NewGuid();
        CharacterId = characterId;
        ActivityType = activityType;
        Status = status;
        StartAtUtc = startAtUtc;
        PlannedEndAtUtc = plannedEndAtUtc;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        CancellationReason = cancellationReason;
        Metadata = metadata;
        CreatedAtUtc = createdAtUtc ?? startAtUtc;
        Version = version == 0 ? 1u : version;
    }

    /// <summary>
    /// Transitions a Scheduled activity to Active.
    /// </summary>
    public void Start(DateTime currentSimulationTimeUtc)
    {
        if (Status == LifeActivityStatus.Active)
            return;

        if (Status == LifeActivityStatus.Completed)
            throw new InvalidOperationException("Cannot transition a completed activity to Active.");

        if (Status == LifeActivityStatus.Cancelled)
            throw new InvalidOperationException("Cannot transition a cancelled activity to Active.");

        if (currentSimulationTimeUtc < StartAtUtc)
            throw new InvalidOperationException($"Cannot start activity before its planned StartAtUtc ({StartAtUtc:O}). Current simulation time: {currentSimulationTimeUtc:O}.");

        Status = LifeActivityStatus.Active;
        StartedAtUtc = currentSimulationTimeUtc;
        Version++;
    }

    /// <summary>
    /// Transitions an Active activity to Completed.
    /// </summary>
    public void Complete(DateTime currentSimulationTimeUtc)
    {
        if (Status == LifeActivityStatus.Completed)
            return;

        if (Status == LifeActivityStatus.Scheduled)
            throw new InvalidOperationException("Cannot transition a scheduled activity directly to Completed. It must be started first.");

        if (Status == LifeActivityStatus.Cancelled)
            throw new InvalidOperationException("Cannot transition a cancelled activity to Completed.");

        if (StartedAtUtc.HasValue && currentSimulationTimeUtc < StartedAtUtc.Value)
            throw new InvalidOperationException($"Cannot complete activity before its StartedAtUtc ({StartedAtUtc.Value:O}). Current simulation time: {currentSimulationTimeUtc:O}.");

        Status = LifeActivityStatus.Completed;
        CompletedAtUtc = currentSimulationTimeUtc;
        Version++;
    }

    /// <summary>
    /// Cancels a Scheduled or Active activity.
    /// </summary>
    public void Cancel(DateTime currentSimulationTimeUtc, string? reason = null)
    {
        if (Status == LifeActivityStatus.Cancelled)
            return;

        if (Status == LifeActivityStatus.Completed)
            throw new InvalidOperationException("Cannot cancel an activity that is already completed.");

        Status = LifeActivityStatus.Cancelled;
        CancellationReason = reason;
        Version++;
    }
}
