using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
    public async Task ConcurrentWorkers_OptimisticConcurrency_AllowsExactlyOneWinner()
    {
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var sceneKey = "throne_room";

        // Seed initial record
        await using (var db = new ProjectDbContext(_options))
        {
            var initialRecord = new SceneVisualStateRecord(
                sessionId: sessionId,
                characterId: charId,
                sceneKey: sceneKey,
                sceneRevision: 1,
                stateJson: "{\"location\":\"Throne Room\",\"version\":1}",
                fingerprint: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                version: 1
            );
            await db.SceneVisualStates.AddAsync(initialRecord);
            await db.SaveChangesAsync();
        }

        int successCount = 0;
        int conflictCount = 0;

        // 10 concurrent workers attempt to transition revision 1 -> revision 2
        var tasks = Enumerable.Range(1, 10).Select(async workerIndex =>
        {
            await using var workerDb = new ProjectDbContext(_options);
            var record = await workerDb.SceneVisualStates
                .FirstOrDefaultAsync(r => r.SessionId == sessionId && r.SceneKey == sceneKey);

            if (record != null)
            {
                try
                {
                    record.UpdateState(
                        newStateJson: $"{{\"location\":\"Throne Room\",\"worker\":{workerIndex}}}",
                        newFingerprint: $"fingerprint_worker_{workerIndex:D2}00000000000000000000000000000000000000",
                        newRevision: 2,
                        turnId: Guid.NewGuid(),
                        newVersion: record.Version + 1
                    );

                    await workerDb.SaveChangesAsync();
                    Interlocked.Increment(ref successCount);
                }
                catch (DbUpdateConcurrencyException)
                {
                    Interlocked.Increment(ref conflictCount);
                }
            }
        });

        await Task.WhenAll(tasks);

        // Verify exactly one worker succeeded and 9 conflicted
        Assert.Equal(1, successCount);
        Assert.Equal(9, conflictCount);

        // Verify final state in DB has Version == 2 and SceneRevision == 2
        await using (var db = new ProjectDbContext(_options))
        {
            var finalRecord = await db.SceneVisualStates.FirstAsync(r => r.SessionId == sessionId && r.SceneKey == sceneKey);
            Assert.Equal(2, finalRecord.SceneRevision);
            Assert.Equal(2u, finalRecord.Version);
        }
    }
}
