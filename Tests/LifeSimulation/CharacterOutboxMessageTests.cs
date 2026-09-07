using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.LifeSimulation;
using Application.Interfaces;
using Application.Services.LifeSimulation;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.LifeSimulation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tests.LifeSimulation;

public sealed class CharacterOutboxMessageTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterOutboxMessageTests()
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

    private (ICharacterLifeActivityRepository activityRepo, ICharacterOutboxRepository outboxRepo, ILifeSimulationService service, FakeLifeSimulationClock clock) CreateSystem(
        CoreDbContext db,
        FakeLifeSimulationClock? clock = null)
    {
        var clk = clock ?? new FakeLifeSimulationClock();
        var activityRepo = new CharacterLifeActivityRepository(db);
        var outboxRepo = new CharacterOutboxRepository(db);
        var service = new LifeSimulationService(activityRepo, outboxRepo, clk);
        return (activityRepo, outboxRepo, service, clk);
    }

    #region 1-4. Domain Lifecycle & Validation Tests

    [Fact]
    public void Test01_OutboxMessage_HasValidIdentity_And_EventIdDiffersFromId()
    {
        var eventId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var occurredAt = DateTime.UtcNow;

        var fingerprint = CanonicalOutboxFingerprint.Compute(
            eventId, characterId, "ActivityStarted", activityId, LifeActivityType.Work, occurredAt);

        var msg = new CharacterOutboxMessage(
            eventId: eventId,
            characterId: characterId,
            eventType: "ActivityStarted",
            payloadJson: "{\"test\":\"factual\"}",
            fingerprint: fingerprint,
            occurredAtUtc: occurredAt
        );

        Assert.NotEqual(Guid.Empty, msg.Id);
        Assert.NotEqual(msg.Id, msg.EventId);
        Assert.Equal(eventId, msg.EventId);
        Assert.Equal(characterId, msg.CharacterId);
        Assert.Equal(CharacterOutboxStatus.Pending, msg.Status);
        Assert.Equal(0, msg.AttemptCount);
        Assert.Equal(1u, msg.Version);
    }

    [Fact]
    public void Test02_Domain_ValidStatusTransitions_WorkCorrectly()
    {
        var msg = new CharacterOutboxMessage(
            eventId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            eventType: "ActivityStarted",
            payloadJson: "{}",
            fingerprint: "fp1",
            occurredAtUtc: DateTime.UtcNow
        );

        // Pending -> Processing
        msg.MarkProcessing();
        Assert.Equal(CharacterOutboxStatus.Processing, msg.Status);
        Assert.Equal(2u, msg.Version);

        // Processing -> Published
        var publishedAt = DateTime.UtcNow;
        msg.MarkPublished(publishedAt);
        Assert.Equal(CharacterOutboxStatus.Published, msg.Status);
        Assert.Equal(publishedAt, msg.ProcessedAtUtc);
        Assert.Equal(3u, msg.Version);
    }

    [Fact]
    public void Test03_Domain_InvalidStatusTransitions_AreRejected()
    {
        var msg = new CharacterOutboxMessage(
            eventId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            eventType: "ActivityStarted",
            payloadJson: "{}",
            fingerprint: "fp1",
            occurredAtUtc: DateTime.UtcNow
        );

        msg.MarkPublished(DateTime.UtcNow);

        // Invariant: Published message cannot transition to Processing, Failed, or Retry
        Assert.Throws<InvalidOperationException>(() => msg.MarkProcessing());
        Assert.Throws<InvalidOperationException>(() => msg.MarkFailed("err", DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => msg.Retry());
    }

    [Fact]
    public void Test04_Domain_FailedMessage_CanBeRetried_WhenEligible()
    {
        var msg = new CharacterOutboxMessage(
            eventId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            eventType: "ActivityStarted",
            payloadJson: "{}",
            fingerprint: "fp1",
            occurredAtUtc: DateTime.UtcNow,
            maxRetries: 2
        );

        msg.MarkProcessing();
        msg.MarkFailed("Network timeout", DateTime.UtcNow, canRetry: true);

        // Attempt 1 < MaxRetries 2 -> reverts to Pending for retry
        Assert.Equal(CharacterOutboxStatus.Pending, msg.Status);
        Assert.Equal(1, msg.AttemptCount);

        msg.MarkProcessing();
        msg.MarkFailed("Network timeout 2", DateTime.UtcNow, canRetry: true);

        // Attempt 2 >= MaxRetries 2 -> transitions to Failed
        Assert.Equal(CharacterOutboxStatus.Failed, msg.Status);
        Assert.Equal(2, msg.AttemptCount);

        // Explicit retry manually resets to Pending
        msg.Retry();
        Assert.Equal(CharacterOutboxStatus.Pending, msg.Status);
    }

    #endregion

    #region 5-11. Repository & Idempotency Tests

    [Fact]
    public async Task Test05_Repository_CanPersistAndRetrieve_OutboxMessage()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, _, _) = CreateSystem(db);

        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var msg = new CharacterOutboxMessage(
            eventId: eventId,
            characterId: charId,
            eventType: "ActivityStarted",
            payloadJson: "{\"factual\":true}",
            fingerprint: "fp",
            occurredAtUtc: DateTime.UtcNow
        );

        await outboxRepo.AddAsync(msg);
        await outboxRepo.SaveChangesAsync();

        var retrieved = await outboxRepo.GetByEventIdAsync(eventId);
        Assert.NotNull(retrieved);
        Assert.Equal(msg.Id, retrieved.Id);
        Assert.Equal(charId, retrieved.CharacterId);
        Assert.Equal("ActivityStarted", retrieved.EventType);
    }

    [Fact]
    public async Task Test06_Repository_CanRetrievePendingMessages()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, _, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var msg1 = new CharacterOutboxMessage(Guid.NewGuid(), charId, "Event1", "{}", "fp1", now);
        var msg2 = new CharacterOutboxMessage(Guid.NewGuid(), charId, "Event2", "{}", "fp2", now.AddMinutes(1));

        await outboxRepo.AddRangeAsync(new[] { msg1, msg2 });
        await outboxRepo.SaveChangesAsync();

        var pending = await outboxRepo.GetPendingMessagesAsync(charId);
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public async Task Test07_Repository_PendingRetrieval_IsDeterministic()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, _, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

        var msg3 = new CharacterOutboxMessage(Guid.NewGuid(), charId, "E3", "{}", "fp3", baseTime.AddHours(2), baseTime.AddMinutes(5));
        var msg1 = new CharacterOutboxMessage(Guid.NewGuid(), charId, "E1", "{}", "fp1", baseTime, baseTime);
        var msg2 = new CharacterOutboxMessage(Guid.NewGuid(), charId, "E2", "{}", "fp2", baseTime.AddHours(1), baseTime.AddMinutes(1));

        // Insert in scrambled order
        await outboxRepo.AddRangeAsync(new[] { msg3, msg1, msg2 });
        await outboxRepo.SaveChangesAsync();

        var pending = await outboxRepo.GetPendingMessagesAsync(charId);
        Assert.Equal(3, pending.Count);
        Assert.Equal("E1", pending[0].EventType);
        Assert.Equal("E2", pending[1].EventType);
        Assert.Equal("E3", pending[2].EventType);
    }

    [Fact]
    public async Task Test08_Repository_CharacterScopedRetrieval_DoesNotLeak()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, _, _) = CreateSystem(db);

        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var msgA = new CharacterOutboxMessage(Guid.NewGuid(), charA, "EventA", "{}", "fpA", now);
        var msgB = new CharacterOutboxMessage(Guid.NewGuid(), charB, "EventB", "{}", "fpB", now);

        await outboxRepo.AddRangeAsync(new[] { msgA, msgB });
        await outboxRepo.SaveChangesAsync();

        var pendingA = await outboxRepo.GetPendingMessagesAsync(charA);
        Assert.Single(pendingA);
        Assert.Equal(charA, pendingA[0].CharacterId);

        var pendingB = await outboxRepo.GetPendingMessagesAsync(charB);
        Assert.Single(pendingB);
        Assert.Equal(charB, pendingB[0].CharacterId);
    }

    [Fact]
    public async Task Test09_Repository_EventIdUniqueness_IsEnforcedAtDatabaseLevel()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, _, _) = CreateSystem(db);

        var sharedEventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var msg1 = new CharacterOutboxMessage(sharedEventId, charId, "TypeA", "{}", "fp1", now);
        var msg2 = new CharacterOutboxMessage(sharedEventId, charId, "TypeB", "{}", "fp2", now);

        await outboxRepo.AddAsync(msg1);
        await outboxRepo.SaveChangesAsync();

        await outboxRepo.AddAsync(msg2);
        await Assert.ThrowsAsync<DbUpdateException>(async () => await outboxRepo.SaveChangesAsync());
    }

    [Fact]
    public async Task Test10_Repository_SameEventId_SamePayload_IsIdempotent()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, _, _) = CreateSystem(db);

        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var msg1 = new CharacterOutboxMessage(eventId, charId, "TypeA", "{\"key\":\"val\"}", "fp_same", now);
        var msg2 = new CharacterOutboxMessage(eventId, charId, "TypeA", "{\"key\":\"val\"}", "fp_same", now);

        var saved1 = await outboxRepo.AddOrGetAsync(msg1);
        var saved2 = await outboxRepo.AddOrGetAsync(msg2);

        Assert.Equal(saved1.Id, saved2.Id);
        Assert.Equal(saved1.EventId, saved2.EventId);
    }

    [Fact]
    public async Task Test11_Repository_SameEventId_DifferentPayload_ThrowsConflictException()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, _, _) = CreateSystem(db);

        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var msg1 = new CharacterOutboxMessage(eventId, charId, "TypeA", "{\"key\":\"val1\"}", "fp_one", now);
        var msg2 = new CharacterOutboxMessage(eventId, charId, "TypeA", "{\"key\":\"val2\"}", "fp_two", now);

        await outboxRepo.AddOrGetAsync(msg1);

        var ex = await Assert.ThrowsAsync<CharacterOutboxIdempotencyConflictException>(async () =>
        {
            await outboxRepo.AddOrGetAsync(msg2);
        });

        Assert.Equal(eventId, ex.EventId);
        Assert.Equal("fp_one", ex.StoredFingerprint);
        Assert.Equal("fp_two", ex.IncomingFingerprint);
    }

    #endregion

    #region 12-15. Transactional Boundary & Atomicity Tests

    [Fact]
    public async Task Test12_Transaction_ActivityTransition_And_OutboxCommit_Atomically()
    {
        using var db = CreateDbContext();
        var (activityRepo, outboxRepo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

        // Schedule activity [10:00 -> 12:00]
        await service.ScheduleActivityAsync(charId, LifeActivityType.Work, baseTime, baseTime.AddHours(2));

        // Tick at 10:00: starts activity and creates outbox message
        var tickContext = new CharacterLifeSimulationContext(charId, baseTime, Guid.NewGuid());
        var result = await service.TickAsync(tickContext);

        Assert.Single(result.StartedActivities);
        Assert.Single(result.Events);

        // Verify activity is Active in DB
        var activeActivity = await activityRepo.GetActiveActivityAsync(charId);
        Assert.NotNull(activeActivity);
        Assert.Equal(LifeActivityStatus.Active, activeActivity.Status);

        // Verify OutboxMessage exists in DB
        var pendingMessages = await outboxRepo.GetPendingMessagesAsync(charId);
        Assert.Single(pendingMessages);
        Assert.Equal("ActivityStarted", pendingMessages[0].EventType);
    }

    [Fact]
    public async Task Test13_Transaction_OutboxFailure_RollsBack_ActivityTransition()
    {
        using var db = CreateDbContext();
        var (activityRepo, outboxRepo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var tickId = Guid.NewGuid();

        var activity = await service.ScheduleActivityAsync(charId, LifeActivityType.Work, baseTime, baseTime.AddHours(2));

        // Deterministically pre-insert an outbox record with conflicting payload for the exact start event
        var raw = $"LifeSimEvent:{tickId:D}:{activity.Id:D}:ActivityStarted";
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        var deterministicEventId = new Guid(hash);

        // Insert colliding message with divergent fingerprint directly to trigger DB uniqueness conflict
        var poisonMsg = new CharacterOutboxMessage(
            eventId: deterministicEventId,
            characterId: charId,
            eventType: "ActivityStarted",
            payloadJson: "{\"poison\":true}",
            fingerprint: "poison_fp",
            occurredAtUtc: baseTime
        );
        await outboxRepo.AddAsync(poisonMsg);
        await outboxRepo.SaveChangesAsync();

        // Now run tick with the same tickId: will attempt to insert start event with same eventId
        var tickContext = new CharacterLifeSimulationContext(charId, baseTime, tickId);
        await Assert.ThrowsAsync<DbUpdateException>(async () => await service.TickAsync(tickContext));

        // Re-read activity from fresh DbContext to verify rollback
        using var freshDb = CreateDbContext();
        var freshActivityRepo = new CharacterLifeActivityRepository(freshDb);
        var persisted = await freshActivityRepo.GetByIdAsync(activity.Id);
        Assert.NotNull(persisted);
        // Status should still be Scheduled, not Active!
        Assert.Equal(LifeActivityStatus.Scheduled, persisted.Status);
    }

    [Fact]
    public async Task Test15_Transaction_MultipleEvents_FromSameTick_AreCommittedAtomically()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

        // Schedule Work [10:00 -> 12:00]
        await service.ScheduleActivityAsync(charId, LifeActivityType.Work, baseTime, baseTime.AddHours(2));
        // Start Work at 10:00
        await service.TickAsync(new CharacterLifeSimulationContext(charId, baseTime, Guid.NewGuid()));

        // Schedule Lunch [12:00 -> 13:00]
        await service.ScheduleActivityAsync(charId, LifeActivityType.Eat, baseTime.AddHours(2), baseTime.AddHours(3));

        // Tick at 12:00: completes Work and starts Lunch in a single transaction
        var tick12 = new CharacterLifeSimulationContext(charId, baseTime.AddHours(2), Guid.NewGuid());
        var result12 = await service.TickAsync(tick12);

        Assert.Single(result12.CompletedActivities);
        Assert.Single(result12.StartedActivities);
        Assert.Equal(2, result12.Events.Count);

        var pendingMessages = await outboxRepo.GetPendingMessagesAsync(charId, limit: 10);
        // Total messages: 1 from 10:00 (Start Work) + 2 from 12:00 (Complete Work, Start Lunch) = 3
        Assert.Equal(3, pendingMessages.Count);
        Assert.Contains(pendingMessages, m => m.EventType == "ActivityCompleted");
        Assert.Contains(pendingMessages, m => m.EventType == "ActivityStarted" && m.OccurredAtUtc == baseTime.AddHours(2));
    }

    #endregion

    #region 16-18. Concurrency Tests

    [Fact]
    public async Task Test16_Concurrency_ConcurrentSameEventIdInsertion_YieldsExactlyOneRecord()
    {
        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db1 = CreateDbContext();
        using var db2 = CreateDbContext();
        var repo1 = new CharacterOutboxRepository(db1);
        var repo2 = new CharacterOutboxRepository(db2);

        var msg1 = new CharacterOutboxMessage(eventId, charId, "ActivityStarted", "{\"f\":1}", "fp_ident", now);
        var msg2 = new CharacterOutboxMessage(eventId, charId, "ActivityStarted", "{\"f\":1}", "fp_ident", now);

        // Concurrent execution
        var task1 = repo1.AddOrGetAsync(msg1);
        var task2 = repo2.AddOrGetAsync(msg2);

        var res1 = await task1;
        var res2 = await task2;

        Assert.Equal(res1.EventId, res2.EventId);

        using var verifyDb = CreateDbContext();
        var count = await verifyDb.CharacterOutboxMessages.CountAsync(m => m.EventId == eventId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Test17_Concurrency_ConcurrentSameEventId_DivergentPayload_ThrowsConflict()
    {
        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db1 = CreateDbContext();
        var repo1 = new CharacterOutboxRepository(db1);
        var msg1 = new CharacterOutboxMessage(eventId, charId, "ActivityStarted", "{\"payload\":\"canonical\"}", "fp_canonical", now);
        await repo1.AddOrGetAsync(msg1);

        using var db2 = CreateDbContext();
        var repo2 = new CharacterOutboxRepository(db2);
        var msg2 = new CharacterOutboxMessage(eventId, charId, "ActivityStarted", "{\"payload\":\"divergent\"}", "fp_divergent", now);

        await Assert.ThrowsAsync<CharacterOutboxIdempotencyConflictException>(async () =>
        {
            await repo2.AddOrGetAsync(msg2);
        });
    }

    [Fact]
    public async Task Test18_Concurrency_ConcurrentDifferentEventIds_BothPersist()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db1 = CreateDbContext();
        using var db2 = CreateDbContext();
        var repo1 = new CharacterOutboxRepository(db1);
        var repo2 = new CharacterOutboxRepository(db2);

        var msg1 = new CharacterOutboxMessage(Guid.NewGuid(), charId, "TypeA", "{}", "fpA", now);
        var msg2 = new CharacterOutboxMessage(Guid.NewGuid(), charId, "TypeB", "{}", "fpB", now);

        await Task.WhenAll(repo1.AddOrGetAsync(msg1), repo2.AddOrGetAsync(msg2));

        using var verifyDb = CreateDbContext();
        var total = await verifyDb.CharacterOutboxMessages.CountAsync(m => m.CharacterId == charId);
        Assert.Equal(2, total);
    }

    #endregion

    #region 19-23. LifeSimulation Integration Tests

    [Fact]
    public async Task Test19_LifeSimulation_TickCompletingActivity_CreatesOutboxEvent()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

        await service.ScheduleActivityAsync(charId, LifeActivityType.Sleep, baseTime, baseTime.AddHours(1));
        await service.TickAsync(new CharacterLifeSimulationContext(charId, baseTime, Guid.NewGuid()));

        // Complete at 11:00
        var tickResult = await service.TickAsync(new CharacterLifeSimulationContext(charId, baseTime.AddHours(1), Guid.NewGuid()));

        Assert.Single(tickResult.CompletedActivities);
        var outbox = await outboxRepo.GetPendingMessagesAsync(charId, limit: 10);
        Assert.Contains(outbox, m => m.EventType == "ActivityCompleted" && m.OccurredAtUtc == baseTime.AddHours(1));
    }

    [Fact]
    public async Task Test20_LifeSimulation_TickStartingActivity_CreatesOutboxEvent()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

        await service.ScheduleActivityAsync(charId, LifeActivityType.Work, baseTime, baseTime.AddHours(2));
        var tickResult = await service.TickAsync(new CharacterLifeSimulationContext(charId, baseTime, Guid.NewGuid()));

        Assert.Single(tickResult.StartedActivities);
        var outbox = await outboxRepo.GetPendingMessagesAsync(charId);
        Assert.Single(outbox);
        Assert.Equal("ActivityStarted", outbox[0].EventType);
    }

    [Fact]
    public async Task Test22_LifeSimulation_NoTransition_CreatesZeroOutboxMessages()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

        // Tick when no activities are scheduled or active
        var tickResult = await service.TickAsync(new CharacterLifeSimulationContext(charId, baseTime, Guid.NewGuid()));

        Assert.Empty(tickResult.Events);
        var outbox = await outboxRepo.GetPendingMessagesAsync(charId);
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task Test23_LifeSimulation_DeterministicTickReplay_DoesNotCreateDuplicateOutboxEvents()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var tickId = Guid.NewGuid();

        await service.ScheduleActivityAsync(charId, LifeActivityType.Work, baseTime, baseTime.AddHours(2));

        // First tick
        var res1 = await service.TickAsync(new CharacterLifeSimulationContext(charId, baseTime, tickId));
        Assert.Single(res1.StartedActivities);
        Assert.Single(res1.Events);

        // Replay same tick with same tickId: activity is already Active so no transition occurs
        var res2 = await service.TickAsync(new CharacterLifeSimulationContext(charId, baseTime, tickId));
        Assert.Empty(res2.StartedActivities);
        Assert.Empty(res2.Events);

        var outboxCount = await db.CharacterOutboxMessages.CountAsync(m => m.CharacterId == charId);
        Assert.Equal(1, outboxCount);
    }

    #endregion

    #region 24-30. Boundary & Architectural Invariant Tests

    [Fact]
    public async Task Test24_Boundary_OutboxPayload_ContainsFactualLifeSimulationDataOnly()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

        await service.ScheduleActivityAsync(charId, LifeActivityType.Work, baseTime, baseTime.AddHours(2));
        await service.TickAsync(new CharacterLifeSimulationContext(charId, baseTime, Guid.NewGuid()));

        var messages = await outboxRepo.GetPendingMessagesAsync(charId);
        Assert.Single(messages);

        var payload = CharacterOutboxPayload.FromJson(messages[0].PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(1, payload.SchemaVersion);
        Assert.Equal(charId, payload.CharacterId);
        Assert.Equal("ActivityStarted", payload.EventType);
        Assert.Equal(LifeActivityType.Work, payload.ActivityType);
        Assert.NotEmpty(payload.Description);
    }

    [Fact]
    public async Task Test25_Boundary_OutboxPayload_DoesNotContainCharacterStateMetrics()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

        await service.ScheduleActivityAsync(charId, LifeActivityType.Work, baseTime, baseTime.AddHours(2));
        await service.TickAsync(new CharacterLifeSimulationContext(charId, baseTime, Guid.NewGuid()));

        var msg = (await outboxRepo.GetPendingMessagesAsync(charId))[0];
        using var doc = JsonDocument.Parse(msg.PayloadJson);
        var root = doc.RootElement;

        // Invariant: strictly factual, NO physiological or psychological state
        Assert.False(root.TryGetProperty("Mood", out _));
        Assert.False(root.TryGetProperty("Energy", out _));
        Assert.False(root.TryGetProperty("Hunger", out _));
        Assert.False(root.TryGetProperty("Stress", out _));
        Assert.False(root.TryGetProperty("SocialNeed", out _));
        Assert.False(root.TryGetProperty("Comfort", out _));
        Assert.False(root.TryGetProperty("Emotion", out _));
        Assert.False(root.TryGetProperty("Desire", out _));
        Assert.False(root.TryGetProperty("Intent", out _));
        Assert.False(root.TryGetProperty("Trust", out _));
        Assert.False(root.TryGetProperty("Affection", out _));
    }

    [Fact]
    public void Test26_Boundary_WorldCognitiveEventAdapter_PreservesEventId()
    {
        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var simEvent = new LifeSimulationEvent(
            EventId: eventId,
            CharacterId: charId,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            ActivityId: Guid.NewGuid(),
            ActivityType: LifeActivityType.Work,
            EventType: "ActivityStarted",
            Description: "Work started."
        );

        var cognitiveEvent = LifeSimulationCognitiveEventAdapter.ToCognitiveEvent(simEvent);

        Assert.Equal(eventId, cognitiveEvent.EventId);
        Assert.Equal(charId, cognitiveEvent.CharacterId);
        Assert.Equal("LifeSimulation", cognitiveEvent.Source);
        Assert.Equal("LifeActivity", cognitiveEvent.Category);
    }

    [Fact]
    public void Test27_Boundary_WorldCognitiveEventAdapter_DoesNotInjectStateMetrics()
    {
        var simEvent = new LifeSimulationEvent(
            EventId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            ActivityId: Guid.NewGuid(),
            ActivityType: LifeActivityType.Rest,
            EventType: "ActivityCompleted",
            Description: "Rest finished."
        );

        var cognitiveEvent = LifeSimulationCognitiveEventAdapter.ToCognitiveEvent(simEvent);

        Assert.Equal(typeof(WorldCognitiveEvent), cognitiveEvent.GetType());
        Assert.Equal("LifeActivity_Rest_ActivityCompleted", cognitiveEvent.EventName);
    }

    [Fact]
    public async Task Test28_29_30_Boundary_IdentitySeparation_Enforced()
    {
        using var db = CreateDbContext();
        var (_, outboxRepo, service, _) = CreateSystem(db);

        var charId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var tickId = Guid.NewGuid();

        var scheduled = await service.ScheduleActivityAsync(charId, LifeActivityType.Work, baseTime, baseTime.AddHours(2));
        var tickResult = await service.TickAsync(new CharacterLifeSimulationContext(charId, baseTime, tickId));

        var outboxMessage = (await outboxRepo.GetPendingMessagesAsync(charId))[0];
        var simEvent = tickResult.Events[0];

        // Invariant: strict identity separation
        Assert.NotEqual(simEvent.EventId, outboxMessage.Id);          // EventId != OutboxMessageId
        Assert.NotEqual(simEvent.EventId, tickId);                    // EventId != TickId
        Assert.NotEqual(simEvent.EventId, scheduled.Id);              // EventId != ActivityId
        Assert.NotEqual(outboxMessage.Id, tickId);                    // OutboxMessageId != TickId
        Assert.NotEqual(outboxMessage.Id, scheduled.Id);              // OutboxMessageId != ActivityId
    }

    #endregion
}
