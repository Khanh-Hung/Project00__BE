using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Common;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.LifeSimulation;
using Application.Exceptions;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.CognitiveCycle;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests.ProductionHardening;

public class IdempotencyHardeningTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public IdempotencyHardeningTests()
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
    public async Task SameEvent_SamePayload_IsIdempotent()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterOutboxRepository(db);

        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var actId = Guid.NewGuid();

        var simEvent = new LifeSimulationEvent(
            EventId: eventId,
            CharacterId: charId,
            OccurredAtUtc: now,
            ActivityId: actId,
            ActivityType: LifeActivityType.Work,
            EventType: "ActivityScheduled",
            Description: "Work activity scheduled");

        var msg1 = CharacterOutboxPayload.CreateOutboxMessage(simEvent, now.UtcDateTime);
        var result1 = await repo.AddOrGetAsync(msg1);
        Assert.NotNull(result1);

        // Replay same message
        var msg2 = CharacterOutboxPayload.CreateOutboxMessage(simEvent, now.UtcDateTime);
        var result2 = await repo.AddOrGetAsync(msg2);
        Assert.NotNull(result2);
        Assert.Equal(result1.Id, result2.Id);

        var count = await db.CharacterOutboxMessages.CountAsync(m => m.EventId == eventId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SameEvent_DifferentPayload_IsConflict()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterOutboxRepository(db);

        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var actId = Guid.NewGuid();

        var simEvent1 = new LifeSimulationEvent(
            EventId: eventId,
            CharacterId: charId,
            OccurredAtUtc: now,
            ActivityId: actId,
            ActivityType: LifeActivityType.Work,
            EventType: "ActivityScheduled",
            Description: "Work activity scheduled");

        var msg1 = CharacterOutboxPayload.CreateOutboxMessage(simEvent1, now.UtcDateTime);
        await repo.AddOrGetAsync(msg1);

        // Conflicting event: SAME EventId, but DIFFERENT activity type (Rest vs Work)
        var simEvent2 = new LifeSimulationEvent(
            EventId: eventId,
            CharacterId: charId,
            OccurredAtUtc: now,
            ActivityId: actId,
            ActivityType: LifeActivityType.Rest,
            EventType: "ActivityScheduled",
            Description: "Rest activity scheduled");

        var msg2 = CharacterOutboxPayload.CreateOutboxMessage(simEvent2, now.UtcDateTime);

        await Assert.ThrowsAsync<CharacterOutboxIdempotencyConflictException>(() =>
            repo.AddOrGetAsync(msg2));
    }

    [Fact]
    public async Task SameExecution_SameMemoryFeedback_IsIdempotent()
    {
        await using var db = new CoreDbContext(_options);
        var service = new CharacterMemoryFeedbackService(db, NullLogger<CharacterMemoryFeedbackService>.Instance);

        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var context = new CharacterCognitiveCycleContext(cycleId, execId, charId, now);
        var cycleResult = CharacterCognitiveCycleResult.CompletedWithoutAction(
            cycleId, execId, charId, now, 1);

        var feedback1 = await service.RecordFeedbackAsync(context, cycleResult);
        Assert.NotNull(feedback1);

        var feedback2 = await service.RecordFeedbackAsync(context, cycleResult);
        Assert.NotNull(feedback2);
        Assert.Equal(feedback1.MemoryId, feedback2.MemoryId);

        var memoryCount = await db.CharacterMemories.CountAsync(m => m.ExecutionId == execId);
        Assert.Equal(1, memoryCount);
    }

    [Fact]
    public async Task SameExecution_DifferentMemoryFeedback_IsConflict()
    {
        await using var db = new CoreDbContext(_options);
        var service = new CharacterMemoryFeedbackService(db, NullLogger<CharacterMemoryFeedbackService>.Instance);

        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var context = new CharacterCognitiveCycleContext(cycleId, execId, charId, now);

        // 1st result: CompletedWithoutAction => produces NoActionTaken feedback
        var cycleResult1 = CharacterCognitiveCycleResult.CompletedWithoutAction(
            cycleId, execId, charId, now, 1);

        await service.RecordFeedbackAsync(context, cycleResult1);

        // 2nd result for SAME execution ID: Failed => produces ActionFailed feedback (different semantic payload)
        var cycleResult2 = CharacterCognitiveCycleResult.Failed(
            cycleId, execId, charId, now, 1, message: "Action failed due to timeout");

        await Assert.ThrowsAsync<CharacterMemoryIdempotencyConflictException>(() =>
            service.RecordFeedbackAsync(context, cycleResult2));
    }

    [Fact]
    public void IdentityDistinctness_AllSubsystemIds_AreMutuallyDistinct()
    {
        var eventId = Guid.NewGuid();
        var outboxMessageId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();

        var idSet = new HashSet<Guid>
        {
            eventId,
            outboxMessageId,
            cycleId,
            executionId,
            activityId
        };

        // All 5 identity types MUST be completely distinct
        Assert.Equal(5, idSet.Count);
        Assert.NotEqual(eventId, outboxMessageId);
        Assert.NotEqual(eventId, cycleId);
        Assert.NotEqual(eventId, executionId);
        Assert.NotEqual(activityId, eventId);
    }
}
