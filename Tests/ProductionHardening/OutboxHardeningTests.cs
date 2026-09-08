using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Contracts.LifeSimulation;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.LifeSimulation;
using Infrastructure.Services.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.LifeSimulation;
using Xunit;

namespace Project.Tests.ProductionHardening;

public class OutboxHardeningTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public OutboxHardeningTests()
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

    [Fact]
    public async Task ActivityAndOutbox_CommitAtomically()
    {
        await using var db = new CoreDbContext(_options);
        var charId = Guid.NewGuid();
        var simTime = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);

        var simClock = new FakeLifeSimulationClock(simTime);
        var systemClock = new SystemClock();
        var activityRepo = new CharacterLifeActivityRepository(db);
        var outboxRepo = new CharacterOutboxRepository(db);

        var service = new LifeSimulationService(
            activityRepo,
            outboxRepo,
            simClock,
            systemClock,
            NullLogger<LifeSimulationService>.Instance);

        // Schedule an activity starting at simTime
        var activity = await service.ScheduleActivityAsync(
            charId,
            LifeActivityType.Work,
            startAtUtc: simTime,
            plannedEndAtUtc: simTime.AddHours(2),
            metadata: "Morning Shift");

        Assert.Equal(LifeActivityStatus.Scheduled, activity.Status);

        // Advance clock to start activity and tick
        var tickResult = await service.TickAsync(new CharacterLifeSimulationContext(charId, simTime, Guid.NewGuid()));

        Assert.True(tickResult.IsSuccess);
        Assert.Single(tickResult.StartedActivities);

        // Verify activity transitioned to Active AND Outbox message committed
        var dbActivity = await db.CharacterLifeActivities.FirstAsync(a => a.Id == activity.Id);
        Assert.Equal(LifeActivityStatus.Active, dbActivity.Status);

        var outboxMessages = await db.CharacterOutboxMessages.Where(m => m.CharacterId == charId).ToListAsync();
        Assert.Single(outboxMessages);
        Assert.Equal("ActivityStarted", outboxMessages[0].EventType);
        Assert.Equal(CharacterOutboxStatus.Pending, outboxMessages[0].Status);
    }

    [Fact]
    public void PublishedMessage_CannotSilentlyReset()
    {
        var now = DateTime.UtcNow;
        var msg = new CharacterOutboxMessage(
            id: Guid.NewGuid(),
            eventId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            eventType: "TestEvent",
            payloadJson: "{}",
            fingerprint: "fp",
            occurredAtUtc: now,
            createdAtUtc: now);

        msg.MarkProcessing();
        msg.MarkPublished(now);

        Assert.Equal(CharacterOutboxStatus.Published, msg.Status);

        // Invariant: Published message cannot transition to Processing, Failed, or Retry
        Assert.Throws<InvalidOperationException>(() => msg.MarkProcessing());
        Assert.Throws<InvalidOperationException>(() => msg.MarkFailed("error", now));
        Assert.Throws<InvalidOperationException>(() => msg.Retry());
    }

    [Fact]
    public void ProcessingMessage_CannotSilentlyBecomePending()
    {
        var now = DateTime.UtcNow;
        var msg = new CharacterOutboxMessage(
            id: Guid.NewGuid(),
            eventId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            eventType: "TestEvent",
            payloadJson: "{}",
            fingerprint: "fp",
            occurredAtUtc: now,
            createdAtUtc: now);

        msg.MarkProcessing();
        Assert.Equal(CharacterOutboxStatus.Processing, msg.Status);

        // Cannot call Retry directly while in Processing (must fail first)
        Assert.Throws<InvalidOperationException>(() => msg.Retry());

        // Marking failed without auto-retry leaves it in Failed status
        msg.MarkFailed("Network timeout", now, canRetry: false);
        Assert.Equal(CharacterOutboxStatus.Failed, msg.Status);

        msg.Retry();
        Assert.Equal(CharacterOutboxStatus.Pending, msg.Status);
    }

    [Fact]
    public async Task OutboxOrdering_PendingMessages_OrderedByOccurredThenCreatedThenId()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterOutboxRepository(db);

        var charId = Guid.NewGuid();
        var t0 = new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);

        var eventA = Guid.NewGuid();
        var eventB = Guid.NewGuid();
        var eventC = Guid.NewGuid();

        // Message A: Occurred T+2, Created T+0
        var simEventA = new LifeSimulationEvent(eventA, charId, t2, Guid.NewGuid(), LifeActivityType.Work, "A", "Msg A");
        var msgA = CharacterOutboxPayload.CreateOutboxMessage(simEventA, createdAtUtc: t0);

        // Message B: Occurred T+1, Created T+1
        var simEventB = new LifeSimulationEvent(eventB, charId, t1, Guid.NewGuid(), LifeActivityType.Work, "B", "Msg B");
        var msgB = CharacterOutboxPayload.CreateOutboxMessage(simEventB, createdAtUtc: t1);

        // Message C: Occurred T+1, Created T+0
        var simEventC = new LifeSimulationEvent(eventC, charId, t1, Guid.NewGuid(), LifeActivityType.Work, "C", "Msg C");
        var msgC = CharacterOutboxPayload.CreateOutboxMessage(simEventC, createdAtUtc: t0);

        // Add out of order: A, then B, then C
        await repo.AddAsync(msgA);
        await repo.AddAsync(msgB);
        await repo.AddAsync(msgC);
        await repo.SaveChangesAsync();

        // Fetch pending messages
        var pending = await repo.GetPendingMessagesAsync(characterId: charId);

        Assert.Equal(3, pending.Count);

        // Expected deterministic order:
        // 1st: Msg C (Occurred T1, Created T0)
        // 2nd: Msg B (Occurred T1, Created T1)
        // 3rd: Msg A (Occurred T2, Created T0)
        Assert.Equal(eventC, pending[0].EventId);
        Assert.Equal(eventB, pending[1].EventId);
        Assert.Equal(eventA, pending[2].EventId);
    }

    [Fact]
    public async Task LifeSimulationService_UsesSha256_ForDeterministicEventGuid()
    {
        await using var db = new CoreDbContext(_options);
        db.EnsureLifeSimulationTriggersCreated();

        var simTime = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        var simClock = new FakeLifeSimulationClock(simTime);
        var systemClock = new SystemClock();
        var activityRepo = new CharacterLifeActivityRepository(db);
        var outboxRepo = new CharacterOutboxRepository(db);

        var service = new LifeSimulationService(
            activityRepo,
            outboxRepo,
            simClock,
            systemClock,
            NullLogger<LifeSimulationService>.Instance);

        var charId = Guid.NewGuid();
        var character = new Character(
            "Test", "Title", "https://example.com/avatar.jpg",
            "Prompt", "Hi", "Category") { Id = charId };
        db.Characters.Add(character);
        var state = new CharacterState(charId, DateTime.UtcNow);
        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();

        var activity = await service.ScheduleActivityAsync(
            charId,
            LifeActivityType.Work,
            startAtUtc: simTime,
            plannedEndAtUtc: simTime.AddHours(2));

        var tickId = Guid.NewGuid();
        var tickResult = await service.TickAsync(new CharacterLifeSimulationContext(
            TickId: tickId,
            CharacterId: charId,
            SimulationTimeUtc: simTime));

        var outboxMsg = await db.CharacterOutboxMessages.FirstAsync(m => m.EventType == "ActivityStarted");

        // Verify that the event ID matches SHA-256 computation
        var raw = $"LifeSimEvent:{tickId:D}:{activity.Id:D}:ActivityStarted";
        var expectedHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        var expectedGuid = new Guid(expectedHash.AsSpan(0, 16));

        Assert.Equal(expectedGuid, outboxMsg.EventId);
    }
}
