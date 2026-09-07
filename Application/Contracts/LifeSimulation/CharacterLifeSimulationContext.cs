using System;

namespace Application.Contracts.LifeSimulation;

/// <summary>
/// Execution context for a character life simulation tick.
/// </summary>
public sealed record CharacterLifeSimulationContext(
    Guid CharacterId,
    DateTimeOffset SimulationTimeUtc,
    Guid TickId
);
