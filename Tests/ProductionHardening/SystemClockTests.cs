using Application.Abstractions.Time;
using Infrastructure.Services.Time;
using Tests.LifeSimulation;
using Xunit;

namespace Project.Tests.ProductionHardening;

public class SystemClockTests
{
    private sealed class FakeSystemClock : ISystemClock
    {
        public DateTimeOffset CurrentTime { get; set; } = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => CurrentTime;
        public DateTime UtcDateTime => UtcNow.UtcDateTime;
    }

    [Fact]
    public void SystemClock_ProductionImplementation_ReturnsValidUtcTime()
    {
        var clock = new SystemClock();
        var before = DateTimeOffset.UtcNow;
        var now = clock.UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.True(now >= before);
        Assert.True(now <= after);
        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.Equal(DateTimeKind.Utc, clock.UtcDateTime.Kind);
    }

    [Fact]
    public void SystemClock_IsDeterministicWithFakeClock()
    {
        var fakeClock = new FakeSystemClock();
        var fixedTime = new DateTimeOffset(2026, 9, 8, 15, 30, 0, TimeSpan.Zero);
        fakeClock.CurrentTime = fixedTime;

        Assert.Equal(fixedTime, fakeClock.UtcNow);
        Assert.Equal(fixedTime.UtcDateTime, fakeClock.UtcDateTime);

        var advancedTime = fixedTime.AddHours(2);
        fakeClock.CurrentTime = advancedTime;

        Assert.Equal(advancedTime, fakeClock.UtcNow);
        Assert.Equal(advancedTime.UtcDateTime, fakeClock.UtcDateTime);
    }

    [Fact]
    public void LifeSimulationClock_IsIndependentFromSystemClock()
    {
        var systemClock = new FakeSystemClock
        {
            CurrentTime = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero)
        };

        var simClock = new FakeLifeSimulationClock(new DateTimeOffset(2050, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // Advance simulation clock by 10 virtual days
        simClock.Advance(TimeSpan.FromDays(10));

        // System clock must remain completely unaffected by virtual world time progression
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero), systemClock.UtcNow);
        Assert.Equal(new DateTimeOffset(2050, 1, 11, 0, 0, 0, TimeSpan.Zero), simClock.UtcNow);
    }
}
