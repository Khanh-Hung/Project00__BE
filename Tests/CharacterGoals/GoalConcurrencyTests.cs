using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services.Goals;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class GoalConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public GoalConcurrencyTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task ConcurrentProgressUpdates_EnforcesOptimisticConcurrencyFencing()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "Master Swordsmanship", CharacterGoalType.SkillDevelopment, 100);

        using (var db = new CoreDbContext(_options))
        {
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

        // Two workers load the same Goal at Version 1
        using var dbWorker1 = new CoreDbContext(_options);
        using var dbWorker2 = new CoreDbContext(_options);

        var goal1 = await dbWorker1.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
        var goal2 = await dbWorker2.CharacterGoals.FirstAsync(g => g.Id == goal.Id);

        // Worker 1 records progress -> advances Version to 2
        goal1.RecordProgress(10);
        await dbWorker1.SaveChangesAsync();

        // Worker 2 attempts to save with stale Version 1 -> DbUpdateConcurrencyException
        goal2.RecordProgress(10);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbWorker2.SaveChangesAsync());
    }
}
