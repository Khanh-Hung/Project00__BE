using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.LifeSimulation;
using Domain.Entities;
using Domain.Enums;

namespace Application.Interfaces;

public interface ILifeSimulationService
{
    /// <summary>
    /// Executes a simulation tick for a character at the specified simulation context time.
    /// Handles completing expired activities and starting newly due scheduled activities.
    /// Emits LifeSimulationEvents deterministically.
    /// </summary>
    Task<LifeSimulationTickResult> TickAsync(CharacterLifeSimulationContext context, CancellationToken ct = default);

    /// <summary>
    /// Schedules a life activity for a character.
    /// Validates start < plannedEnd and rejects any overlapping activities.
    /// </summary>
    Task<CharacterLifeActivity> ScheduleActivityAsync(
        Guid characterId,
        LifeActivityType activityType,
        DateTimeOffset startAtUtc,
        DateTimeOffset plannedEndAtUtc,
        string? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels an existing scheduled or active activity.
    /// </summary>
    Task<CharacterLifeActivity> CancelActivityAsync(
        Guid activityId,
        DateTimeOffset currentSimulationTimeUtc,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves the current active activity of a character, if any.
    /// </summary>
    Task<CharacterLifeActivity?> GetCurrentActivityAsync(Guid characterId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves scheduled activities for a character.
    /// </summary>
    Task<IReadOnlyList<CharacterLifeActivity>> GetScheduleAsync(Guid characterId, CancellationToken ct = default);
}
