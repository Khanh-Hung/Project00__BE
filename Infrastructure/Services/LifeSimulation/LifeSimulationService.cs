using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Abstractions.Time;
using Application.Contracts.LifeSimulation;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.LifeSimulation;

public sealed class LifeSimulationService : ILifeSimulationService
{
    private readonly ICharacterLifeActivityRepository _repository;
    private readonly ILifeSimulationClock _clock;

    public LifeSimulationService(
        ICharacterLifeActivityRepository repository,
        ILifeSimulationClock clock)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<LifeSimulationTickResult> TickAsync(
        CharacterLifeSimulationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.CharacterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(context));

        var simTime = context.SimulationTimeUtc.UtcDateTime;
        var completedActivities = new List<CharacterLifeActivity>();
        var startedActivities = new List<CharacterLifeActivity>();
        var events = new List<LifeSimulationEvent>();
        CharacterLifeActivity? currentActive = null;

        try
        {
            // 1. Process active activities
            var activeActivities = await _repository.GetActiveActivitiesAsync(context.CharacterId, ct);
            foreach (var active in activeActivities)
            {
                if (active.PlannedEndAtUtc <= simTime)
                {
                    active.Complete(simTime);
                    completedActivities.Add(active);

                    var eventId = ComputeDeterministicEventGuid(context.TickId, active.Id, "ActivityCompleted");
                    events.Add(new LifeSimulationEvent(
                        eventId,
                        context.CharacterId,
                        context.SimulationTimeUtc,
                        active.Id,
                        active.ActivityType,
                        "ActivityCompleted",
                        $"Activity {active.ActivityType} completed."
                    ));
                }
                else
                {
                    currentActive = active;
                }
            }

            // 2. If no active activity remains, check if any scheduled activity is due
            if (currentActive == null)
            {
                var nextScheduled = await _repository.GetNextScheduledActivityAsync(context.CharacterId, simTime, ct);
                if (nextScheduled != null)
                {
                    if (nextScheduled.PlannedEndAtUtc <= simTime)
                    {
                        // Completely expired before tick
                        nextScheduled.Start(nextScheduled.StartAtUtc);
                        startedActivities.Add(nextScheduled);
                        var startEventId = ComputeDeterministicEventGuid(context.TickId, nextScheduled.Id, "ActivityStarted");
                        events.Add(new LifeSimulationEvent(
                            startEventId,
                            context.CharacterId,
                            new DateTimeOffset(nextScheduled.StartAtUtc, TimeSpan.Zero),
                            nextScheduled.Id,
                            nextScheduled.ActivityType,
                            "ActivityStarted",
                            $"Activity {nextScheduled.ActivityType} started."
                        ));

                        nextScheduled.Complete(nextScheduled.PlannedEndAtUtc);
                        completedActivities.Add(nextScheduled);
                        var completeEventId = ComputeDeterministicEventGuid(context.TickId, nextScheduled.Id, "ActivityCompleted");
                        events.Add(new LifeSimulationEvent(
                            completeEventId,
                            context.CharacterId,
                            new DateTimeOffset(nextScheduled.PlannedEndAtUtc, TimeSpan.Zero),
                            nextScheduled.Id,
                            nextScheduled.ActivityType,
                            "ActivityCompleted",
                            $"Activity {nextScheduled.ActivityType} completed."
                        ));
                    }
                    else
                    {
                        nextScheduled.Start(simTime);
                        startedActivities.Add(nextScheduled);
                        currentActive = nextScheduled;

                        var startEventId = ComputeDeterministicEventGuid(context.TickId, nextScheduled.Id, "ActivityStarted");
                        events.Add(new LifeSimulationEvent(
                            startEventId,
                            context.CharacterId,
                            context.SimulationTimeUtc,
                            nextScheduled.Id,
                            nextScheduled.ActivityType,
                            "ActivityStarted",
                            $"Activity {nextScheduled.ActivityType} started."
                        ));
                    }
                }
            }

            if (completedActivities.Count > 0 || startedActivities.Count > 0)
            {
                await _repository.SaveChangesAsync(ct);
            }

            return new LifeSimulationTickResult(
                SimulationTickId: context.TickId,
                CharacterId: context.CharacterId,
                SimulationTimeUtc: context.SimulationTimeUtc,
                ActiveActivity: currentActive,
                CompletedActivities: completedActivities,
                StartedActivities: startedActivities,
                Events: events,
                IsSuccess: true
            );
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new LifeSimulationConcurrencyException(context.CharacterId, null, "Optimistic concurrency conflict occurred during simulation tick.", ex);
        }
    }

    public async Task<CharacterLifeActivity> ScheduleActivityAsync(
        Guid characterId,
        LifeActivityType activityType,
        DateTimeOffset startAtUtc,
        DateTimeOffset plannedEndAtUtc,
        string? metadata = null,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        var start = startAtUtc.UtcDateTime;
        var plannedEnd = plannedEndAtUtc.UtcDateTime;

        if (start >= plannedEnd)
            throw new ArgumentException($"StartAtUtc ({start:O}) must be strictly before PlannedEndAtUtc ({plannedEnd:O}).", nameof(startAtUtc));

        try
        {
            var conflicting = await _repository.GetOverlappingActivityAsync(characterId, start, plannedEnd, null, ct);
            if (conflicting != null)
            {
                throw new LifeActivityScheduleConflictException(characterId, start, plannedEnd, conflicting.Id);
            }

            var activity = new CharacterLifeActivity(
                characterId: characterId,
                activityType: activityType,
                startAtUtc: start,
                plannedEndAtUtc: plannedEnd,
                status: LifeActivityStatus.Scheduled,
                metadata: metadata,
                createdAtUtc: _clock.UtcDateTime
            );

            await _repository.AddAsync(activity, ct);
            await _repository.SaveChangesAsync(ct);

            return activity;
        }
        catch (DbUpdateException ex)
        {
            var conflicting = await _repository.GetOverlappingActivityAsync(characterId, start, plannedEnd, null, ct);
            throw new LifeActivityScheduleConflictException(characterId, start, plannedEnd, conflicting?.Id,
                message: $"Activity for character {characterId} from {start:O} to {plannedEnd:O} overlaps with existing activity {conflicting?.Id}.",
                innerException: ex);
        }
    }

    public async Task<CharacterLifeActivity> CancelActivityAsync(
        Guid activityId,
        DateTimeOffset currentSimulationTimeUtc,
        string? reason = null,
        CancellationToken ct = default)
    {
        var activity = await _repository.GetByIdAsync(activityId, ct)
            ?? throw new KeyNotFoundException($"CharacterLifeActivity {activityId} not found.");

        activity.Cancel(currentSimulationTimeUtc.UtcDateTime, reason);
        await _repository.SaveChangesAsync(ct);

        return activity;
    }

    public async Task<CharacterLifeActivity?> GetCurrentActivityAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _repository.GetActiveActivityAsync(characterId, ct);
    }

    public async Task<IReadOnlyList<CharacterLifeActivity>> GetScheduleAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _repository.GetScheduledActivitiesAsync(characterId, ct);
    }

    private static Guid ComputeDeterministicEventGuid(Guid tickId, Guid activityId, string eventType)
    {
        var raw = $"LifeSimEvent:{tickId:D}:{activityId:D}:{eventType}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        return new Guid(hash);
    }
}
