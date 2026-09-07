using System;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.LifeSimulation;

namespace Application.Services.LifeSimulation;

/// <summary>
/// Converts pure life simulation events into cognitive cycle events.
/// Strictly factual: does NOT mutate CharacterState or invent emotional/need values.
/// </summary>
public static class LifeSimulationCognitiveEventAdapter
{
    public static WorldCognitiveEvent ToCognitiveEvent(LifeSimulationEvent simulationEvent)
    {
        ArgumentNullException.ThrowIfNull(simulationEvent);

        return new WorldCognitiveEvent(
            EventId: simulationEvent.EventId,
            CharacterId: simulationEvent.CharacterId,
            OccurredAtUtc: simulationEvent.OccurredAtUtc,
            EventName: $"LifeActivity_{simulationEvent.ActivityType}_{simulationEvent.EventType}",
            Source: "LifeSimulation",
            Category: "LifeActivity"
        );
    }
}
