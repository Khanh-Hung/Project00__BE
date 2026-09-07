using System;
using Application.Abstractions.Time;

namespace Tests.LifeSimulation;

public sealed class FakeLifeSimulationClock : ILifeSimulationClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

    public void Advance(TimeSpan duration)
    {
        UtcNow = UtcNow.Add(duration);
    }

    public void Set(DateTimeOffset time)
    {
        UtcNow = time;
    }
}
