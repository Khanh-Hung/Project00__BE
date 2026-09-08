using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Common;
using Domain.Common;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests.ProductionHardening;

public class ConcurrencyHardeningTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public ConcurrencyHardeningTests()
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
    public async Task ConcurrentStateMutation_DoesNotSilentlyOverwrite()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now, hunger: 50m, energy: 80m, stress: 20m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        // Two independent DbContext instances load the same state at version 1
        await using var db1 = new CoreDbContext(_options);
        await using var db2 = new CoreDbContext(_options);

        var state1 = await db1.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        var state2 = await db2.CharacterStates.FirstAsync(s => s.CharacterId == charId);

        Assert.Equal(1, state1.Version);
        Assert.Equal(1, state2.Version);

        // Worker 1 modifies and successfully commits
        state1.ApplyDelta(new CharacterStateDelta(hungerDelta: 10m));
        await db1.SaveChangesAsync();

        // Worker 2 attempts to save stale entity
        state2.ApplyDelta(new CharacterStateDelta(hungerDelta: 20m));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task ConcurrentRelationshipMutation_DoesNotSilentlyOverwrite()
    {
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = Clock.Now;

        await using (var db = new CoreDbContext(_options))
        {
            var rel = CharacterRelationship.Create(
                charId,
                userId,
                initialAffection: 10,
                initialMood: CharacterMood.Neutral,
                initialMoodIntensity: 20,
                initialTimestamp: now);

            db.CharacterRelationships.Add(rel);
            await db.SaveChangesAsync();
        }

        await using var db1 = new CoreDbContext(_options);
        await using var db2 = new CoreDbContext(_options);

        var rel1 = await db1.CharacterRelationships.FirstAsync(r => r.CharacterId == charId && r.UserId == userId);
        var rel2 = await db2.CharacterRelationships.FirstAsync(r => r.CharacterId == charId && r.UserId == userId);

        Assert.Equal(1u, rel1.Version);
        Assert.Equal(1u, rel2.Version);

        // Worker 1 applies affection delta and succeeds
        rel1.ApplyAffectionDelta(5, now);
        await db1.SaveChangesAsync();

        // Worker 2 tries to commit on stale version
        rel2.ApplyAffectionDelta(-5, now);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task ConcurrentPersonalityMutation_DoesNotSilentlyOverwrite()
    {
        var charId = Guid.NewGuid();

        await using (var db = new CoreDbContext(_options))
        {
            var personality = new CharacterPersonality(charId, warmth: 50, openness: 50);
            db.CharacterPersonalities.Add(personality);
            await db.SaveChangesAsync();
        }

        await using var db1 = new CoreDbContext(_options);
        await using var db2 = new CoreDbContext(_options);

        var p1 = await db1.CharacterPersonalities.FirstAsync(p => p.CharacterId == charId);
        var p2 = await db2.CharacterPersonalities.FirstAsync(p => p.CharacterId == charId);

        Assert.Equal(1u, p1.Version);
        Assert.Equal(1u, p2.Version);

        // Worker 1 adapts trait
        p1.AdaptTrait(PersonalityTraitKeys.Warmth, 1);
        await db1.SaveChangesAsync();

        // Worker 2 attempts to save on stale version
        p2.AdaptTrait(PersonalityTraitKeys.Warmth, -1);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task ConcurrentLifeSimulationMutation_DoesNotSilentlyOverwrite()
    {
        var charId = Guid.NewGuid();
        var start = DateTime.UtcNow;
        var end = start.AddHours(1);

        var activity = new CharacterLifeActivity(charId, LifeActivityType.Work, start, end);

        await using (var db = new CoreDbContext(_options))
        {
            db.CharacterLifeActivities.Add(activity);
            await db.SaveChangesAsync();
        }

        await using var db1 = new CoreDbContext(_options);
        await using var db2 = new CoreDbContext(_options);

        var a1 = await db1.CharacterLifeActivities.FirstAsync(a => a.Id == activity.Id);
        var a2 = await db2.CharacterLifeActivities.FirstAsync(a => a.Id == activity.Id);

        // Worker 1 transitions activity
        a1.Start(start);
        await db1.SaveChangesAsync();

        // Worker 2 transitions activity concurrently
        a2.Cancel(start, "Cancelled by user");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task ConcurrentOutboxDuplicate_EventId_ProducesOneLogicalMessage()
    {
        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var msg1 = new CharacterOutboxMessage(
            id: Guid.NewGuid(),
            eventId: eventId,
            characterId: charId,
            eventType: "ActivityScheduled",
            payloadJson: "{\"ActivityId\":\"" + Guid.NewGuid() + "\"}",
            fingerprint: "fingerprint_1",
            occurredAtUtc: now,
            createdAtUtc: now);

        var msg2 = new CharacterOutboxMessage(
            id: Guid.NewGuid(),
            eventId: eventId, // DUPLICATE EventId
            characterId: charId,
            eventType: "ActivityScheduled",
            payloadJson: "{\"ActivityId\":\"" + Guid.NewGuid() + "\"}",
            fingerprint: "fingerprint_2",
            occurredAtUtc: now,
            createdAtUtc: now);

        await using (var db1 = new CoreDbContext(_options))
        {
            db1.CharacterOutboxMessages.Add(msg1);
            await db1.SaveChangesAsync();
        }

        // Inserting second message with identical EventId MUST fail unique constraint
        await using var db2 = new CoreDbContext(_options);
        db2.CharacterOutboxMessages.Add(msg2);
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public void GenericRepository_ThrowsInvalidOperationException_WhenEntityNotMappedInCoreDbContext()
    {
        using var db = new CoreDbContext(_options);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new GenericRepository<User>(db));

        Assert.Contains("User", ex.Message);
        Assert.Contains("not mapped in CoreDbContext", ex.Message);
    }

    [Fact]
    public void UnitOfWork_GetRepository_ThrowsInvalidOperationException_ForUnmappedEntity()
    {
        using var db = new CoreDbContext(_options);
        var uow = new UnitOfWork(db);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            uow.GetRepository<User>());

        Assert.Contains("User", ex.Message);
        Assert.Contains("not mapped in CoreDbContext", ex.Message);
    }
}
