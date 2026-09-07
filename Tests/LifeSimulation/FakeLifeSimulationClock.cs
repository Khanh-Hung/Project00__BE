using System;
using Application.Abstractions.Time;

namespace Tests.LifeSimulation;

public sealed class FakeLifeSimulationClock : ILifeSimulationClock
{
    public DateTimeOffset UtcNow { get; set; }

    public FakeLifeSimulationClock(DateTimeOffset? initialTime = null)
    {
        UtcNow = initialTime ?? new DateTimeOffset(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    }

    public void Advance(TimeSpan duration)
    {
        UtcNow = UtcNow.Add(duration);
    }

    public void Set(DateTimeOffset time)
    {
        UtcNow = time;
    }
}
