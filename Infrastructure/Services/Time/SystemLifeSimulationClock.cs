using System;
using Application.Abstractions.Time;

namespace Infrastructure.Services.Time;

/// <summary>
/// System production implementation of ILifeSimulationClock using real UTC time.
/// </summary>
public sealed class SystemLifeSimulationClock : ILifeSimulationClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
