using System;

namespace Application.Abstractions.Time;

/// <summary>
/// Authoritative simulation clock abstraction.
/// Decouples simulation time from system time for deterministic, replayable execution.
/// </summary>
public interface ILifeSimulationClock
{
    DateTimeOffset UtcNow { get; }
    DateTime UtcDateTime => UtcNow.UtcDateTime;
}
