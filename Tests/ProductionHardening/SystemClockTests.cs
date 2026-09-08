using Application.Abstractions.Time;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.LifeSimulation;
using Infrastructure.Services.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void SystemClock_ImplementsIDateTimeProvider_ConsolidatedTime()
    {
        var clock = new SystemClock();
        IDateTimeProvider dateTimeProvider = clock;

        var clockUtc = clock.UtcDateTime;
        var providerUtc = dateTimeProvider.UtcNow;

        Assert.Equal(DateTimeKind.Utc, providerUtc.Kind);
        Assert.True(Math.Abs((clockUtc - providerUtc).TotalMilliseconds) < 50);
    }

    [Fact]
    public void LifeSimulationService_Requires_MandatorySystemClock_ThrowsArgumentNullExceptionWhenNull()
    {
        var db = new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var activityRepo = new CharacterLifeActivityRepository(db);
        var outboxRepo = new CharacterOutboxRepository(db);
        var simClock = new FakeLifeSimulationClock();
        var logger = NullLogger<LifeSimulationService>.Instance;

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new LifeSimulationService(
                activityRepo,
                outboxRepo,
                simClock,
                systemClock: null!,
                logger: logger));

        Assert.Equal("systemClock", ex.ParamName);
    }

    [Fact]
    public void LifeSimulationService_Requires_MandatoryLogger_ThrowsArgumentNullExceptionWhenNull()
    {
        var db = new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var activityRepo = new CharacterLifeActivityRepository(db);
        var outboxRepo = new CharacterOutboxRepository(db);
        var simClock = new FakeLifeSimulationClock();
        var systemClock = new SystemClock();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new LifeSimulationService(
                activityRepo,
                outboxRepo,
                simClock,
                systemClock: systemClock,
                logger: null!));

        Assert.Equal("logger", ex.ParamName);
    }

    [Fact]
    public async Task LifeSimulationService_UsesInjectedSystemClock_DeterministicOutboxCreatedAtTimestamp()
    {
        var db = new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        db.EnsureLifeSimulationTriggersCreated();

        var activityRepo = new CharacterLifeActivityRepository(db);
        var outboxRepo = new CharacterOutboxRepository(db);

        var simTime = new DateTimeOffset(2050, 6, 15, 8, 0, 0, TimeSpan.Zero);
        var simClock = new FakeLifeSimulationClock(simTime);

        var deterministicRealTime = new DateTimeOffset(2026, 9, 8, 14, 25, 30, TimeSpan.Zero);
        var fakeSystemClock = new FakeSystemClock { CurrentTime = deterministicRealTime };
        var logger = NullLogger<LifeSimulationService>.Instance;

        var service = new LifeSimulationService(
            activityRepo,
            outboxRepo,
            simClock,
            fakeSystemClock,
            logger);

        var charId = Guid.NewGuid();
        var character = new Character(
            "Test", "Title", "https://example.com/avatar.jpg",
            "Prompt", "Hi", "Category") { Id = charId };
        db.Characters.Add(character);
        var state = new CharacterState(charId, DateTime.UtcNow);
        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();

        // Schedule and start an activity
        var activity = await service.ScheduleActivityAsync(
            charId,
            LifeActivityType.Work,
            startAtUtc: simTime,
            plannedEndAtUtc: simTime.AddHours(4));

        var tickResult = await service.TickAsync(new Application.Contracts.LifeSimulation.CharacterLifeSimulationContext(
            TickId: Guid.NewGuid(),
            CharacterId: charId,
            SimulationTimeUtc: simTime));

        // Verify outbox message CreatedAtUtc matches the injected fakeSystemClock deterministic time
        var outboxMsg = await db.CharacterOutboxMessages.FirstAsync(m => m.EventType == "ActivityStarted");
        Assert.Equal(deterministicRealTime.UtcDateTime, outboxMsg.CreatedAtUtc);
        Assert.Equal(simTime.UtcDateTime, outboxMsg.OccurredAtUtc);
    }
}
