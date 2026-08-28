using Domain.Entities;
using Infrastructure.Persistence;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class VisualContinuityConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public VisualContinuityConcurrencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task SaveStateAsync_TwoWorkers_CASConflict_WorkerAWins_WorkerBThrowsDbUpdateConcurrencyException()
    {
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var sceneKey = "alchemy_lab";

        // Seed initial state at Version 5, Revision 5
        await using (var db = new CoreDbContext(_options))
        {
            var reader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var charState = new CharacterVisualState(charId, "Alchemy Lab", 5);
            var stateV5 = new SceneVisualState(
                sessionId: sessionId,
                characterId: charId,
                location: "Alchemy Lab",
                characterState: charState,
                sceneRevision: 5,
                sceneKey: sceneKey,
                version: 5
            );
            await reader.SaveStateAsync(stateV5, expectedVersion: 0);

            var initialRecord = await db.SceneVisualStates.FirstAsync(r => r.SessionId == sessionId && r.SceneKey == sceneKey);
            // Manually align initial version to 5 for test scenario
            initialRecord.UpdateState(initialRecord.StateJson, initialRecord.Fingerprint, 5, Guid.NewGuid(), newVersion: 5);
            await db.SaveChangesAsync();
        }

        // Both Worker A and Worker B read state at Version 5
        await using var dbWorkerA = new CoreDbContext(_options);
        var readerA = new SceneVisualStateReader(dbWorkerA, NullLogger<SceneVisualStateReader>.Instance);
        var stateA = await readerA.GetLatestBySessionAndSceneKeyAsync(sessionId, sceneKey);
        Assert.NotNull(stateA);

        await using var dbWorkerB = new CoreDbContext(_options);
        var readerB = new SceneVisualStateReader(dbWorkerB, NullLogger<SceneVisualStateReader>.Instance);
        var stateB = await readerB.GetLatestBySessionAndSceneKeyAsync(sessionId, sceneKey);
        Assert.NotNull(stateB);

        // Worker A executes SaveStateAsync with expectedVersion = 5 -> SUCCESS, DB becomes Version 6
        var charStateA = new CharacterVisualState(charId, "Alchemy Lab", 6, outfit: "Blue Alchemist Robe");
        var updatedStateA = new SceneVisualState(
            sessionId: sessionId,
            characterId: charId,
            location: "Alchemy Lab",
            characterState: charStateA,
            sceneRevision: 6,
            sceneKey: sceneKey,
            version: 6
        );
        await readerA.SaveStateAsync(updatedStateA, expectedVersion: 5);

        // Verify DB is now Version 6
        await using (var verifyDb = new CoreDbContext(_options))
        {
            var recordAfterA = await verifyDb.SceneVisualStates.FirstAsync(r => r.SessionId == sessionId && r.SceneKey == sceneKey);
            Assert.Equal(6u, recordAfterA.Version);
            Assert.Equal(6, recordAfterA.SceneRevision);
        }

        // Worker B attempts SaveStateAsync with stale expectedVersion = 5 -> MUST THROW DbUpdateConcurrencyException
        var charStateB = new CharacterVisualState(charId, "Alchemy Lab", 6, outfit: "Red Alchemist Robe");
        var updatedStateB = new SceneVisualState(
            sessionId: sessionId,
            characterId: charId,
            location: "Alchemy Lab",
            characterState: charStateB,
            sceneRevision: 6,
            sceneKey: sceneKey,
            version: 6
        );

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            readerB.SaveStateAsync(updatedStateB, expectedVersion: 5));

        // Verify Worker B's stale write was rejected and DB remains Worker A's state
        await using (var verifyDb = new CoreDbContext(_options))
        {
            var finalRecord = await verifyDb.SceneVisualStates.FirstAsync(r => r.SessionId == sessionId && r.SceneKey == sceneKey);
            Assert.Equal(6u, finalRecord.Version);
            Assert.Contains("Blue Alchemist Robe", finalRecord.StateJson);
            Assert.DoesNotContain("Red Alchemist Robe", finalRecord.StateJson);
        }
    }

    [Fact]
    public async Task SaveStateAsync_ConcurrentInitialInserts_EnforcesUniqueIndexInvariant_ExactlyOneWinner()
    {
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var sceneKey = "crystal_sanctuary";

        int successCount = 0;
        int conflictCount = 0;

        // 10 concurrent workers attempt to insert initial state for the SAME (SessionId, SceneKey)
        var tasks = Enumerable.Range(1, 10).Select(async workerIndex =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var workerReader = new SceneVisualStateReader(workerDb, NullLogger<SceneVisualStateReader>.Instance);

            var charState = new CharacterVisualState(charId, "Crystal Sanctuary", 1, outfit: $"Worker_{workerIndex}_Robes");
            var state = new SceneVisualState(
                sessionId: sessionId,
                characterId: charId,
                location: "Crystal Sanctuary",
                characterState: charState,
                sceneRevision: 1,
                sceneKey: sceneKey,
                version: 1
            );

            try
            {
                await workerReader.SaveStateAsync(state, expectedVersion: 0);
                Interlocked.Increment(ref successCount);
            }
            catch (DbUpdateConcurrencyException)
            {
                Interlocked.Increment(ref conflictCount);
            }
        });

        await Task.WhenAll(tasks);

        // Invariant: Exactly 1 worker succeeded, 9 conflicted
        Assert.Equal(1, successCount);
        Assert.Equal(9, conflictCount);

        // Database Invariant: Maximum 1 authoritative current state per (SessionId, SceneKey)
        await using (var db = new CoreDbContext(_options))
        {
            var count = await db.SceneVisualStates.CountAsync(r => r.SessionId == sessionId && r.SceneKey == sceneKey);
            Assert.Equal(1, count);

            var record = await db.SceneVisualStates.FirstAsync(r => r.SessionId == sessionId && r.SceneKey == sceneKey);
            Assert.Equal(1u, record.Version);
            Assert.Equal(1, record.SceneRevision);
        }
    }

    [Fact]
    public async Task SaveStateAsync_ConcurrentWorkers_EnforcesAuthoritativeCAS_AllowsExactlyOneWinner()
    {
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var sceneKey = "throne_room";

        // Seed initial state (Version 1, Revision 1) using production SaveStateAsync
        await using (var db = new CoreDbContext(_options))
        {
            var reader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var charState = new CharacterVisualState(charId, "Throne Room", 1);
            var initialState = new SceneVisualState(
                sessionId: sessionId,
                characterId: charId,
                location: "Throne Room",
                characterState: charState,
                sceneRevision: 1,
                sceneKey: sceneKey,
                version: 1
            );
            await reader.SaveStateAsync(initialState, expectedVersion: 0);
        }

        int successCount = 0;
        int conflictCount = 0;

        // 10 concurrent workers read Version 1 and attempt SaveStateAsync with expectedVersion = 1 to advance to Revision 2
        var tasks = Enumerable.Range(1, 10).Select(async workerIndex =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var workerReader = new SceneVisualStateReader(workerDb, NullLogger<SceneVisualStateReader>.Instance);

            var charState = new CharacterVisualState(charId, "Throne Room", 2);
            var updatedState = new SceneVisualState(
                sessionId: sessionId,
                characterId: charId,
                location: "Throne Room",
                characterState: charState,
                sceneRevision: 2,
                sceneKey: sceneKey,
                version: 2
            );

            try
            {
                // Authoritative CAS with expectedVersion = 1
                await workerReader.SaveStateAsync(updatedState, expectedVersion: 1);
                Interlocked.Increment(ref successCount);
            }
            catch (DbUpdateConcurrencyException)
            {
                Interlocked.Increment(ref conflictCount);
            }
        });

        await Task.WhenAll(tasks);

        // Verify: Exactly 1 worker succeeded with CAS, 9 received DbUpdateConcurrencyException
        Assert.Equal(1, successCount);
        Assert.Equal(9, conflictCount);

        // Verify: Database record has Version == 2 and SceneRevision == 2
        await using (var db = new CoreDbContext(_options))
        {
            var finalRecord = await db.SceneVisualStates.FirstAsync(r => r.SessionId == sessionId && r.SceneKey == sceneKey);
            Assert.Equal(2, finalRecord.SceneRevision);
            Assert.Equal(2u, finalRecord.Version);
        }
    }
}
