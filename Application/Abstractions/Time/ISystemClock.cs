using System;

namespace Application.Abstractions.Time;

/// <summary>
/// Authoritative real-time application clock abstraction.
/// Decouples real UTC time from static system calls for deterministic testing and observability.
/// Invariant: Must remain strictly distinct from ILifeSimulationClock (simulated virtual world time).
/// </summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
    DateTime UtcDateTime => UtcNow.UtcDateTime;
}
