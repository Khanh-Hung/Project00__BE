using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.LifeSimulation;
using Application.Interfaces;
using Application.Services.LifeSimulation;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.LifeSimulation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Tests.LifeSimulation;

public sealed class CharacterLifeActivityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterLifeActivityTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
        db.EnsureLifeSimulationTriggersCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private CoreDbContext CreateDbContext()
    {
        var db = new CoreDbContext(_options);
        db.EnsureLifeSimulationTriggersCreated();
        return db;
    }

    private (ICharacterLifeActivityRepository repo, ILifeSimulationService service, FakeLifeSimulationClock clock) CreateSystem(
        CoreDbContext db,
        FakeLifeSimulationClock? clock = null)
    {
        var clk = clock ?? new FakeLifeSimulationClock();
        var repo = new CharacterLifeActivityRepository(db);
        var outboxRepo = new CharacterOutboxRepository(db);
        var service = new LifeSimulationService(repo, outboxRepo, clk);
        return (repo, service, clk);
    }

    #region 1-6. Domain Lifecycle & Validation Tests

    [Fact]
    public void Test01_Domain_StartAtUtcMustBeStrictlyBeforePlannedEndAtUtc()
    {
        var charId = Guid.NewGuid();
        var now = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

        // Same time
        var exSame = Assert.Throws<ArgumentException>(() =>
            new CharacterLifeActivity(charId, LifeActivityType.Work, now, now));
        Assert.Contains("must be strictly before PlannedEndAtUtc", exSame.Message);

        // Start after end
        var exAfter = Assert.Throws<ArgumentException>(() =>
            new CharacterLifeActivity(charId, LifeActivityType.Work, now.AddHours(1), now));
        Assert.Contains("must be strictly before PlannedEndAtUtc", exAfter.Message);
    }

    [Fact]
    public void Test02_Domain_CharacterIdCannotBeEmpty()
    {
        var now = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var ex = Assert.Throws<ArgumentException>(() =>
            new CharacterLifeActivity(Guid.Empty, LifeActivityType.Work, now, now.AddHours(1)));
        Assert.Contains("CharacterId cannot be empty", ex.Message);
    }

    [Fact]
    public void Test03_Domain_Transition_ScheduledToActive_Succeeds()
    {
        var charId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);

        var activity = new CharacterLifeActivity(charId, LifeActivityType.Rest, start, end);
        Assert.Equal(LifeActivityStatus.Scheduled, activity.Status);
        Assert.Equal(1u, activity.Version);

        var simTime = start.AddMinutes(5);
        activity.Start(simTime);

        Assert.Equal(LifeActivityStatus.Active, activity.Status);
        Assert.Equal(simTime, activity.StartedAtUtc);
        Assert.Equal(2u, activity.Version);

        // Idempotent Start
        activity.Start(simTime.AddMinutes(1));
        Assert.Equal(LifeActivityStatus.Active, activity.Status);
        Assert.Equal(simTime, activity.StartedAtUtc);
        Assert.Equal(2u, activity.Version);
    }

    [Fact]
    public void Test03b_Domain_StartBeforeStartAtUtc_ThrowsInvalidOperationException()
    {
        var charId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);

        var activity = new CharacterLifeActivity(charId, LifeActivityType.Work, start, end);
        var ex = Assert.Throws<InvalidOperationException>(() => activity.Start(start.AddMinutes(-5)));
        Assert.Contains("Cannot start activity before its planned StartAtUtc", ex.Message);
    }

    [Fact]
    public void Test04_Domain_Transition_ActiveToCompleted_Succeeds()
    {
        var charId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);

        var activity = new CharacterLifeActivity(charId, LifeActivityType.Eat, start, end);
        activity.Start(start);
        Assert.Equal(LifeActivityStatus.Active, activity.Status);
        Assert.Equal(2u, activity.Version);

        var completeTime = end;
        activity.Complete(completeTime);

        Assert.Equal(LifeActivityStatus.Completed, activity.Status);
        Assert.Equal(completeTime, activity.CompletedAtUtc);
        Assert.Equal(3u, activity.Version);

        // Idempotent Complete
        activity.Complete(completeTime.AddMinutes(5));
        Assert.Equal(LifeActivityStatus.Completed, activity.Status);
        Assert.Equal(3u, activity.Version);
    }

    [Fact]
    public void Test04b_Domain_CompleteBeforeStartedAtUtc_ThrowsInvalidOperationException()
    {
        var charId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);

        var activity = new CharacterLifeActivity(charId, LifeActivityType.Work, start, end);
        activity.Start(start);
        var ex = Assert.Throws<InvalidOperationException>(() => activity.Complete(start.AddMinutes(-10)));
        Assert.Contains("Cannot complete activity before its StartedAtUtc", ex.Message);
    }

    [Fact]
    public void Test05_Domain_Transition_Cancel_Succeeds()
    {
        var charId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);

        // Cancel scheduled
        var activityScheduled = new CharacterLifeActivity(charId, LifeActivityType.Travel, start, end);
        activityScheduled.Cancel(start.AddMinutes(1), "Change of plans");
        Assert.Equal(LifeActivityStatus.Cancelled, activityScheduled.Status);
        Assert.Equal("Change of plans", activityScheduled.CancellationReason);
        Assert.Equal(2u, activityScheduled.Version);

        // Cancel active
        var activityActive = new CharacterLifeActivity(charId, LifeActivityType.Socialize, start, end);
        activityActive.Start(start);
        activityActive.Cancel(start.AddMinutes(10), "Interrupted");
        Assert.Equal(LifeActivityStatus.Cancelled, activityActive.Status);
        Assert.Equal("Interrupted", activityActive.CancellationReason);
        Assert.Equal(3u, activityActive.Version);
    }

    [Fact]
    public void Test06_Domain_InvalidTransitions_ThrowInvalidOperationException()
    {
        var charId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);

        // Scheduled cannot transition directly to Completed
        var scheduled = new CharacterLifeActivity(charId, LifeActivityType.Work, start, end);
        Assert.Throws<InvalidOperationException>(() => scheduled.Complete(end));

        // Completed cannot transition to Active
        scheduled.Start(start);
        scheduled.Complete(end);
        Assert.Throws<InvalidOperationException>(() => scheduled.Start(end.AddMinutes(1)));

        // Completed cannot be Cancelled
        Assert.Throws<InvalidOperationException>(() => scheduled.Cancel(end.AddMinutes(1)));

        // Cancelled cannot transition to Active or Completed
        var cancelled = new CharacterLifeActivity(charId, LifeActivityType.Sleep, start, end);
        cancelled.Cancel(start);
        Assert.Throws<InvalidOperationException>(() => cancelled.Start(start));
        Assert.Throws<InvalidOperationException>(() => cancelled.Complete(end));
    }

    #endregion

    #region 7-10. Repository Persistence & Retrieval Tests

    [Fact]
    public async Task Test07_Repository_PersistAndRetrieveById()
    {
        using var db = CreateDbContext();
        var (repo, _, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 7, 8, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(2);
        var activity = new CharacterLifeActivity(charId, LifeActivityType.Work, start, end, metadata: "{\"project\":\"Apollo\"}");

        await repo.AddAsync(activity);
        await repo.SaveChangesAsync();

        using var dbVerify = CreateDbContext();
        var (repoVerify, _, _) = CreateSystem(dbVerify);

        var retrieved = await repoVerify.GetByIdAsync(activity.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(activity.Id, retrieved.Id);
        Assert.Equal(charId, retrieved.CharacterId);
        Assert.Equal(LifeActivityType.Work, retrieved.ActivityType);
        Assert.Equal(LifeActivityStatus.Scheduled, retrieved.Status);
        Assert.Equal(start, retrieved.StartAtUtc);
        Assert.Equal(end, retrieved.PlannedEndAtUtc);
        Assert.Equal("{\"project\":\"Apollo\"}", retrieved.Metadata);
        Assert.Equal(1u, retrieved.Version);
    }

    [Fact]
    public async Task Test08_Repository_GetActiveActivity()
    {
        using var db = CreateDbContext();
        var (repo, _, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 7, 8, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(2);

        // Initially no active activity
        var none = await repo.GetActiveActivityAsync(charId);
        Assert.Null(none);

        var scheduled = new CharacterLifeActivity(charId, LifeActivityType.Sleep, start, end);
        await repo.AddAsync(scheduled);
        await repo.SaveChangesAsync();

        // Still none active
        Assert.Null(await repo.GetActiveActivityAsync(charId));

        // Start it
        scheduled.Start(start);
        await repo.SaveChangesAsync();

        var active = await repo.GetActiveActivityAsync(charId);
        Assert.NotNull(active);
        Assert.Equal(scheduled.Id, active.Id);
        Assert.Equal(LifeActivityStatus.Active, active.Status);
    }

    [Fact]
    public async Task Test09_Repository_GetNextScheduledActivity()
    {
        using var db = CreateDbContext();
        var (repo, _, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 8, 0, 0, DateTimeKind.Utc);

        var act1 = new CharacterLifeActivity(charId, LifeActivityType.Eat, baseTime.AddHours(1), baseTime.AddHours(2));
        var act2 = new CharacterLifeActivity(charId, LifeActivityType.Work, baseTime.AddHours(3), baseTime.AddHours(5));

        await repo.AddAsync(act1);
        await repo.AddAsync(act2);
        await repo.SaveChangesAsync();

        // As of baseTime: neither is due
        var dueBefore = await repo.GetNextScheduledActivityAsync(charId, baseTime);
        Assert.Null(dueBefore);

        // As of baseTime + 1 hour: act1 is due
        var dueAct1 = await repo.GetNextScheduledActivityAsync(charId, baseTime.AddHours(1));
        Assert.NotNull(dueAct1);
        Assert.Equal(act1.Id, dueAct1.Id);

        // As of baseTime + 4 hours: act1 is earliest due
        var earliest = await repo.GetNextScheduledActivityAsync(charId, baseTime.AddHours(4));
        Assert.NotNull(earliest);
        Assert.Equal(act1.Id, earliest.Id);
    }

    [Fact]
    public async Task Test10_Repository_IsolationBetweenCharacters()
    {
        using var db = CreateDbContext();
        var (repo, _, _) = CreateSystem(db);

        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();
        var now = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Utc);

        var actA = new CharacterLifeActivity(charA, LifeActivityType.Sleep, now, now.AddHours(8));
        actA.Start(now);
        var actB = new CharacterLifeActivity(charB, LifeActivityType.Work, now, now.AddHours(4));

        await repo.AddAsync(actA);
        await repo.AddAsync(actB);
        await repo.SaveChangesAsync();

        var activeA = await repo.GetActiveActivityAsync(charA);
        var activeB = await repo.GetActiveActivityAsync(charB);

        Assert.NotNull(activeA);
        Assert.Equal(charA, activeA.CharacterId);
        Assert.Equal(LifeActivityType.Sleep, activeA.ActivityType);

        Assert.Null(activeB); // B is scheduled, not active

        var scheduledB = await repo.GetScheduledActivitiesAsync(charB);
        Assert.Single(scheduledB);
        Assert.Equal(charB, scheduledB[0].CharacterId);

        var scheduledA = await repo.GetScheduledActivitiesAsync(charA);
        Assert.Empty(scheduledA);
    }

    #endregion

    #region 11-14. Scheduling & Invariant Tests

    [Fact]
    public async Task Test11_Scheduling_OverlappingScheduledActivity_RejectsWithException()
    {
        using var db = CreateDbContext();
        var (_, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(2);

        await service.ScheduleActivityAsync(charId, LifeActivityType.Work, start, end);

        // Case A: Exact overlap
        await Assert.ThrowsAsync<LifeActivityScheduleConflictException>(() =>
            service.ScheduleActivityAsync(charId, LifeActivityType.Rest, start, end));

        // Case B: Partial overlap (starts during, ends after)
        await Assert.ThrowsAsync<LifeActivityScheduleConflictException>(() =>
            service.ScheduleActivityAsync(charId, LifeActivityType.Rest, start.AddHours(1), end.AddHours(1)));

        // Case C: Partial overlap (starts before, ends during)
        await Assert.ThrowsAsync<LifeActivityScheduleConflictException>(() =>
            service.ScheduleActivityAsync(charId, LifeActivityType.Rest, start.AddHours(-1), start.AddHours(1)));

        // Case D: Strict containment (within the window)
        await Assert.ThrowsAsync<LifeActivityScheduleConflictException>(() =>
            service.ScheduleActivityAsync(charId, LifeActivityType.Rest, start.AddMinutes(30), start.AddMinutes(60)));
    }

    [Fact]
    public async Task Test12_Scheduling_OverlappingAgainstActiveActivity_RejectsWithException()
    {
        using var db = CreateDbContext();
        var (repo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 7, 8, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(3);

        var active = new CharacterLifeActivity(charId, LifeActivityType.Sleep, start, end);
        active.Start(start);
        await repo.AddAsync(active);
        await repo.SaveChangesAsync();

        var conflictStart = new DateTimeOffset(start.AddHours(1), TimeSpan.Zero);
        var conflictEnd = conflictStart.AddHours(1);

        var ex = await Assert.ThrowsAsync<LifeActivityScheduleConflictException>(() =>
            service.ScheduleActivityAsync(charId, LifeActivityType.Eat, conflictStart, conflictEnd));

        Assert.Equal(charId, ex.CharacterId);
        Assert.Equal(active.Id, ex.ConflictingActivityId);
    }

    [Fact]
    public async Task Test13_Scheduling_NonOverlappingActivities_AcceptedAndPersisted()
    {
        using var db = CreateDbContext();
        var (_, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var time1 = new DateTimeOffset(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
        var time2 = time1.AddHours(1);
        var time3 = time2.AddHours(2);
        var time4 = time3.AddHours(1);

        // Consecutive, abutted intervals: [8:00, 9:00) and [9:00, 11:00) and [11:00, 12:00)
        var act1 = await service.ScheduleActivityAsync(charId, LifeActivityType.Eat, time1, time2);
        var act2 = await service.ScheduleActivityAsync(charId, LifeActivityType.Work, time2, time3);
        var act3 = await service.ScheduleActivityAsync(charId, LifeActivityType.Rest, time3, time4);

        var schedule = await service.GetScheduleAsync(charId);
        Assert.Equal(3, schedule.Count);
        Assert.Equal(act1.Id, schedule[0].Id);
        Assert.Equal(act2.Id, schedule[1].Id);
        Assert.Equal(act3.Id, schedule[2].Id);
    }

    [Fact]
    public async Task Test14_Scheduling_DeterministicOrderingOfScheduledActivities()
    {
        using var db = CreateDbContext();
        var (repo, _, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 6, 0, 0, DateTimeKind.Utc);

        // Insert out of chronological order
        var act3 = new CharacterLifeActivity(charId, LifeActivityType.Sleep, baseTime.AddHours(10), baseTime.AddHours(18));
        var act1 = new CharacterLifeActivity(charId, LifeActivityType.Eat, baseTime.AddHours(1), baseTime.AddHours(2));
        var act2 = new CharacterLifeActivity(charId, LifeActivityType.Work, baseTime.AddHours(3), baseTime.AddHours(7));

        await repo.AddAsync(act3);
        await repo.AddAsync(act1);
        await repo.AddAsync(act2);
        await repo.SaveChangesAsync();

        var scheduled = await repo.GetScheduledActivitiesAsync(charId);
        Assert.Equal(3, scheduled.Count);
        Assert.Equal(act1.Id, scheduled[0].Id);
        Assert.Equal(act2.Id, scheduled[1].Id);
        Assert.Equal(act3.Id, scheduled[2].Id);
    }

    #endregion

    #region 15-20. Simulation Tick Tests

    [Fact]
    public async Task Test15_Tick_CompletesExpiredActiveActivity()
    {
        using var db = CreateDbContext();
        var clock = new FakeLifeSimulationClock(new DateTimeOffset(2026, 9, 7, 8, 0, 0, TimeSpan.Zero));
        var (repo, service, _) = CreateSystem(db, clock);

        var charId = Guid.NewGuid();
        var start = clock.UtcNow.UtcDateTime;
        var end = start.AddHours(2);

        var active = new CharacterLifeActivity(charId, LifeActivityType.Work, start, end);
        active.Start(start);
        await repo.AddAsync(active);
        await repo.SaveChangesAsync();

        // Advance clock to end time
        clock.Set(new DateTimeOffset(end, TimeSpan.Zero));

        var tickContext = new CharacterLifeSimulationContext(
            CharacterId: charId,
            SimulationTimeUtc: clock.UtcNow,
            TickId: Guid.NewGuid()
        );

        var result = await service.TickAsync(tickContext);

        Assert.True(result.IsSuccess);
        Assert.Single(result.CompletedActivities);
        Assert.Equal(active.Id, result.CompletedActivities[0].Id);
        Assert.Equal(LifeActivityStatus.Completed, result.CompletedActivities[0].Status);
        Assert.Null(result.ActiveActivity);

        Assert.Single(result.Events);
        Assert.Equal("ActivityCompleted", result.Events[0].EventType);
        Assert.Equal(active.Id, result.Events[0].ActivityId);
        Assert.Equal(LifeActivityType.Work, result.Events[0].ActivityType);
    }

    [Fact]
    public async Task Test16_Tick_ActivatesNextScheduledActivityWhenDue()
    {
        using var db = CreateDbContext();
        var clock = new FakeLifeSimulationClock(new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero));
        var (_, service, _) = CreateSystem(db, clock);

        var charId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(2);

        var scheduled = await service.ScheduleActivityAsync(charId, LifeActivityType.Rest, start, end);

        // Advance clock to after start time
        clock.Set(start.AddMinutes(5));

        var tickContext = new CharacterLifeSimulationContext(
            CharacterId: charId,
            SimulationTimeUtc: clock.UtcNow,
            TickId: Guid.NewGuid()
        );

        var result = await service.TickAsync(tickContext);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ActiveActivity);
        Assert.Equal(scheduled.Id, result.ActiveActivity.Id);
        Assert.Equal(LifeActivityStatus.Active, result.ActiveActivity.Status);
        Assert.Single(result.StartedActivities);

        Assert.Single(result.Events);
        Assert.Equal("ActivityStarted", result.Events[0].EventType);
        Assert.Equal(scheduled.Id, result.Events[0].ActivityId);
    }

    [Fact]
    public async Task Test17_Tick_IgnoresFutureScheduledActivities()
    {
        using var db = CreateDbContext();
        var clock = new FakeLifeSimulationClock(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var (_, service, _) = CreateSystem(db, clock);

        var charId = Guid.NewGuid();
        var futureStart = new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero);
        var futureEnd = futureStart.AddHours(2);

        var scheduled = await service.ScheduleActivityAsync(charId, LifeActivityType.Sleep, futureStart, futureEnd);

        var tickContext = new CharacterLifeSimulationContext(
            CharacterId: charId,
            SimulationTimeUtc: clock.UtcNow,
            TickId: Guid.NewGuid()
        );

        var result = await service.TickAsync(tickContext);

        Assert.True(result.IsSuccess);
        Assert.Null(result.ActiveActivity);
        Assert.Empty(result.StartedActivities);
        Assert.Empty(result.CompletedActivities);
        Assert.Empty(result.Events);

        var current = await service.GetCurrentActivityAsync(charId);
        Assert.Null(current);
    }

    [Fact]
    public async Task Test18_Tick_HandlesBackToBackCompletionAndActivationInSingleTick()
    {
        using var db = CreateDbContext();
        var clock = new FakeLifeSimulationClock(new DateTimeOffset(2026, 9, 7, 8, 0, 0, TimeSpan.Zero));
        var (repo, service, _) = CreateSystem(db, clock);

        var charId = Guid.NewGuid();
        var t1 = clock.UtcNow.UtcDateTime;
        var t2 = t1.AddHours(1);
        var t3 = t2.AddHours(2);

        var active = new CharacterLifeActivity(charId, LifeActivityType.Eat, t1, t2);
        active.Start(t1);
        var next = new CharacterLifeActivity(charId, LifeActivityType.Work, t2, t3);

        await repo.AddAsync(active);
        await repo.AddAsync(next);
        await repo.SaveChangesAsync();

        // Tick occurs exactly at transition boundary t2
        clock.Set(new DateTimeOffset(t2, TimeSpan.Zero));

        var tickContext = new CharacterLifeSimulationContext(
            CharacterId: charId,
            SimulationTimeUtc: clock.UtcNow,
            TickId: Guid.NewGuid()
        );

        var result = await service.TickAsync(tickContext);

        Assert.True(result.IsSuccess);
        Assert.Single(result.CompletedActivities);
        Assert.Equal(active.Id, result.CompletedActivities[0].Id);

        Assert.Single(result.StartedActivities);
        Assert.Equal(next.Id, result.StartedActivities[0].Id);
        Assert.Equal(next.Id, result.ActiveActivity?.Id);

        Assert.Equal(2, result.Events.Count);
        Assert.Equal("ActivityCompleted", result.Events[0].EventType);
        Assert.Equal("ActivityStarted", result.Events[1].EventType);
    }

    [Fact]
    public async Task Test19_Tick_Determinism_ExactSameInputsProduceIdenticalResults()
    {
        var charId = Guid.NewGuid();
        var tickId = Guid.NewGuid();
        var simTime = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
        var actId = Guid.NewGuid();

        // Run on instance 1
        using var conn1 = new SqliteConnection("Filename=:memory:");
        conn1.Open();
        var options1 = new DbContextOptionsBuilder<CoreDbContext>().UseSqlite(conn1).Options;
        using var db1 = new CoreDbContext(options1);
        db1.Database.EnsureCreated();
        db1.EnsureLifeSimulationTriggersCreated();
        var clock1 = new FakeLifeSimulationClock(simTime);
        var (repo1, service1, _) = CreateSystem(db1, clock1);
        var act1 = new CharacterLifeActivity(charId, LifeActivityType.Work, simTime.UtcDateTime.AddHours(-1), simTime.UtcDateTime.AddHours(1), id: actId);
        act1.Start(simTime.UtcDateTime.AddHours(-1));
        await repo1.AddAsync(act1);
        await repo1.SaveChangesAsync();

        var res1 = await service1.TickAsync(new CharacterLifeSimulationContext(charId, simTime, tickId));

        // Run on instance 2 with exact same setup in separate isolated database
        using var conn2 = new SqliteConnection("Filename=:memory:");
        conn2.Open();
        var options2 = new DbContextOptionsBuilder<CoreDbContext>().UseSqlite(conn2).Options;
        using var db2 = new CoreDbContext(options2);
        db2.Database.EnsureCreated();
        db2.EnsureLifeSimulationTriggersCreated();
        var clock2 = new FakeLifeSimulationClock(simTime);
        var (repo2, service2, _) = CreateSystem(db2, clock2);
        var act2 = new CharacterLifeActivity(charId, LifeActivityType.Work, simTime.UtcDateTime.AddHours(-1), simTime.UtcDateTime.AddHours(1), id: actId);
        act2.Start(simTime.UtcDateTime.AddHours(-1));
        await repo2.AddAsync(act2);
        await repo2.SaveChangesAsync();

        var res2 = await service2.TickAsync(new CharacterLifeSimulationContext(charId, simTime, tickId));

        Assert.Equal(res1.ActiveActivity?.Id, res2.ActiveActivity?.Id);
        Assert.Equal(res1.CompletedActivities.Count, res2.CompletedActivities.Count);
        Assert.Equal(res1.StartedActivities.Count, res2.StartedActivities.Count);
        Assert.Equal(res1.Events.Count, res2.Events.Count);
    }

    [Fact]
    public async Task Test20_Tick_FullyElapsedScheduledActivity_CompletesBothTransitions()
    {
        using var db = CreateDbContext();
        var clock = new FakeLifeSimulationClock(new DateTimeOffset(2026, 9, 7, 8, 0, 0, TimeSpan.Zero));
        var (repo, service, _) = CreateSystem(db, clock);

        var charId = Guid.NewGuid();
        var start = clock.UtcNow.UtcDateTime;
        var end = start.AddHours(1);

        // Scheduled activity completely elapsed before tick was called
        var elapsed = new CharacterLifeActivity(charId, LifeActivityType.Rest, start, end);
        await repo.AddAsync(elapsed);
        await repo.SaveChangesAsync();

        clock.Set(new DateTimeOffset(end.AddHours(2), TimeSpan.Zero));
        var result = await service.TickAsync(new CharacterLifeSimulationContext(charId, clock.UtcNow, Guid.NewGuid()));

        Assert.True(result.IsSuccess);
        Assert.Single(result.StartedActivities);
        Assert.Single(result.CompletedActivities);
        Assert.Equal(elapsed.Id, result.CompletedActivities[0].Id);
        Assert.Equal(LifeActivityStatus.Completed, result.CompletedActivities[0].Status);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal("ActivityStarted", result.Events[0].EventType);
        Assert.Equal("ActivityCompleted", result.Events[1].EventType);
    }

    #endregion

    #region 21-22. Non-Mutation / Subsystem Decoupling Invariant Tests

    [Fact]
    public async Task Test21_SimulationInvariant_TickDoesNotMutateCharacterState()
    {
        using var db = CreateDbContext();
        var (repo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();

        // Seed CharacterState
        var state = new CharacterState(
            characterId: charId,
            initializedAtUtc: DateTime.UtcNow,
            hunger: 55m,
            energy: 75m,
            mood: 60m,
            stress: 30m,
            socialNeed: 40m,
            comfort: 70m
        );
        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();

        var stateVersionBefore = state.Version;
        var hungerBefore = state.Hunger;
        var energyBefore = state.Energy;

        // Schedule and run tick
        var start = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        await service.ScheduleActivityAsync(charId, LifeActivityType.Sleep, start, end);

        var tickResult = await service.TickAsync(new CharacterLifeSimulationContext(charId, start.AddMinutes(10), Guid.NewGuid()));
        Assert.True(tickResult.IsSuccess);

        // Reload fresh CharacterState from DB
        using var dbVerify = CreateDbContext();
        var stateAfter = await dbVerify.CharacterStates.FirstOrDefaultAsync(s => s.CharacterId == charId);
        Assert.NotNull(stateAfter);
        Assert.Equal(stateVersionBefore, stateAfter.Version);
        Assert.Equal(hungerBefore, stateAfter.Hunger);
        Assert.Equal(energyBefore, stateAfter.Energy);
    }

    [Fact]
    public async Task Test22_SimulationInvariant_TickDoesNotMutatePersonalityMemoryOrRelationship()
    {
        using var db = CreateDbContext();
        var (_, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();

        // Seed Personality
        var personality = new CharacterPersonality(charId, warmth: 70, openness: 80, assertiveness: 60, conscientiousness: 75, socialConfidence: 65, trustDisposition: 60, emotionalStability: 70);
        db.CharacterPersonalities.Add(personality);

        // Seed Memory
        var memory = CharacterMemory.Create(charId, userId: Guid.NewGuid(), content: "User greeted nicely", type: MemoryType.Fact);
        db.CharacterMemories.Add(memory);

        // Seed Relationship
        var relationship = CharacterRelationship.Create(charId, RelationshipTargetType.User, Guid.NewGuid(), RelationshipType.Friend, trust: 50, affection: 50, familiarity: 50);
        db.CharacterRelationships.Add(relationship);

        await db.SaveChangesAsync();

        var personalityCountBefore = await db.CharacterPersonalities.CountAsync();
        var memoryCountBefore = await db.CharacterMemories.CountAsync();
        var relationshipCountBefore = await db.CharacterRelationships.CountAsync();

        // Run simulation tick
        var tickTime = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        await service.ScheduleActivityAsync(charId, LifeActivityType.Work, tickTime, tickTime.AddHours(2));
        await service.TickAsync(new CharacterLifeSimulationContext(charId, tickTime.AddMinutes(5), Guid.NewGuid()));

        using var dbVerify = CreateDbContext();
        Assert.Equal(personalityCountBefore, await dbVerify.CharacterPersonalities.CountAsync());
        Assert.Equal(memoryCountBefore, await dbVerify.CharacterMemories.CountAsync());
        Assert.Equal(relationshipCountBefore, await dbVerify.CharacterRelationships.CountAsync());
    }

    #endregion

    #region 23-26. Concurrency, Barrier Race, Adapter & Identity Invariant Tests

    [Fact]
    public async Task Test23_Concurrency_OptimisticConcurrencyConflict_ThrowsLifeSimulationConcurrencyException()
    {
        using var db1 = CreateDbContext();
        using var db2 = CreateDbContext();

        var (repo1, service1, _) = CreateSystem(db1);
        var (repo2, service2, _) = CreateSystem(db2);

        var charId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 7, 8, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);

        var act = new CharacterLifeActivity(charId, LifeActivityType.Work, start, end);
        await repo1.AddAsync(act);
        await repo1.SaveChangesAsync();

        // Both contexts load the activity
        var loadedInDb1 = await repo1.GetByIdAsync(act.Id);
        var loadedInDb2 = await repo2.GetByIdAsync(act.Id);
        Assert.NotNull(loadedInDb1);
        Assert.NotNull(loadedInDb2);

        // Db1 starts activity and saves successfully (increments version)
        loadedInDb1.Start(start);
        await repo1.SaveChangesAsync();

        // Db2 attempts to cancel activity with stale original version
        loadedInDb2.Cancel(start.AddMinutes(5), "Conflicting cancel");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => repo2.SaveChangesAsync());
    }

    [Fact]
    public async Task Test26_Scheduling_ConcurrentScheduleRace_OneWinnerAndOneConflictException()
    {
        var charId = Guid.NewGuid();
        var clock = new FakeLifeSimulationClock(new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero));

        // Worker A wants [10:00, 12:00)
        var startA = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
        var endA = startA.AddHours(2);

        // Worker B wants overlapping [11:00, 13:00)
        var startB = new DateTimeOffset(2026, 9, 7, 11, 0, 0, TimeSpan.Zero);
        var endB = startB.AddHours(2);

        // Setup Worker B with an interceptor that deterministicly pauses right before saving,
        // allowing Worker A to race in and commit the overlapping activity first.
        var interceptor = new ConcurrentScheduleRaceInterceptor(async () =>
        {
            await using var dbWorkerA = new CoreDbContext(_options);
            dbWorkerA.EnsureLifeSimulationTriggersCreated();
            var (_, serviceA, _) = CreateSystem(dbWorkerA, clock);

            // Worker A executes and commits successfully
            await serviceA.ScheduleActivityAsync(charId, LifeActivityType.Work, startA, endA);
        });

        var optionsB = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var dbWorkerB = new CoreDbContext(optionsB);
        dbWorkerB.EnsureLifeSimulationTriggersCreated();
        var (_, serviceB, _) = CreateSystem(dbWorkerB, clock);

        // Worker B checks overlap (finds none initially), then in SavingChangesAsync Worker A inserts and commits,
        // then Worker B attempts to commit and is rejected by the database trigger constraint!
        var ex = await Assert.ThrowsAsync<LifeActivityScheduleConflictException>(() =>
            serviceB.ScheduleActivityAsync(charId, LifeActivityType.Sleep, startB, endB));

        Assert.Equal(charId, ex.CharacterId);
        Assert.True(interceptor.InterceptorFired);

        // Verify exactly ONE activity was persisted in DB
        await using var verifyDb = new CoreDbContext(_options);
        var activities = await verifyDb.CharacterLifeActivities
            .Where(a => a.CharacterId == charId)
            .ToListAsync();

        Assert.Single(activities);
        Assert.Equal(LifeActivityType.Work, activities[0].ActivityType);
        Assert.Equal(startA.UtcDateTime, activities[0].StartAtUtc);
    }

    [Fact]
    public void Test24_CognitiveAdapter_ConvertsLifeSimulationEventToValidWorldCognitiveEvent()
    {
        var charId = Guid.NewGuid();
        var actId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var time = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

        var simEvent = new LifeSimulationEvent(
            EventId: eventId,
            CharacterId: charId,
            OccurredAtUtc: time,
            ActivityId: actId,
            ActivityType: LifeActivityType.Sleep,
            EventType: "ActivityStarted",
            Description: "Activity Sleep started."
        );

        var worldEvent = LifeSimulationCognitiveEventAdapter.ToCognitiveEvent(simEvent);

        Assert.NotNull(worldEvent);
        Assert.Equal(eventId, worldEvent.EventId);
        Assert.Equal(charId, worldEvent.CharacterId);
        Assert.Equal(time, worldEvent.OccurredAtUtc);
        Assert.Equal("LifeSimulation", worldEvent.Source);
        Assert.Equal("LifeActivity", worldEvent.Category);
        Assert.Equal("LifeActivity_Sleep_ActivityStarted", worldEvent.EventName);
        Assert.Equal(CognitiveEventType.WorldEvent, worldEvent.EventType);
    }

    [Fact]
    public void Test25_Identity_ActivityIdDifferentFromEventIdAndTickId()
    {
        var tickId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var actId = Guid.NewGuid();
        var time = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

        var simEvent = new LifeSimulationEvent(
            EventId: Guid.NewGuid(),
            CharacterId: charId,
            OccurredAtUtc: time,
            ActivityId: actId,
            ActivityType: LifeActivityType.Work,
            EventType: "ActivityStarted",
            Description: "Work started."
        );

        Assert.NotEqual(simEvent.EventId, simEvent.ActivityId);
        Assert.NotEqual(simEvent.ActivityId, tickId);
        Assert.NotEqual(simEvent.EventId, tickId);
    }

    #endregion

    private sealed class ConcurrentScheduleRaceInterceptor : SaveChangesInterceptor
    {
        private readonly Func<Task> _onBeforeFirstSave;
        private int _invoked;
        public bool InterceptorFired => _invoked > 0;

        public ConcurrentScheduleRaceInterceptor(Func<Task> onBeforeFirstSave)
        {
            _onBeforeFirstSave = onBeforeFirstSave;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _invoked, 1, 0) == 0)
            {
                await _onBeforeFirstSave();
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
