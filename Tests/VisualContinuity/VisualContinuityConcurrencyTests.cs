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
    private readonly DbContextOptions<ProjectDbContext> _options;

    public VisualContinuityConcurrencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new ProjectDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task SaveStateAsync_ConcurrentWorkers_EnforcesAuthoritativeCAS_AllowsExactlyOneWinner()
    {
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var sceneKey = "throne_room";

        // Seed initial state (Version 1, Revision 1) using production SaveStateAsync
        await using (var db = new ProjectDbContext(_options))
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
            await using var workerDb = new ProjectDbContext(_options);
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
        await using (var db = new ProjectDbContext(_options))
        {
            var finalRecord = await db.SceneVisualStates.FirstAsync(r => r.SessionId == sessionId && r.SceneKey == sceneKey);
            Assert.Equal(2, finalRecord.SceneRevision);
            Assert.Equal(2u, finalRecord.Version);
        }
    }
}
