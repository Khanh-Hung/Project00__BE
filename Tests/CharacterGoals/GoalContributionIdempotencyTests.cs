using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class GoalContributionIdempotencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public GoalContributionIdempotencyTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new ProjectDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task DuplicateGoalContribution_IsIdempotentlySuppressed_AndDoesNotDoubleIncrement()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "Learn Alchemy", CharacterGoalType.SkillDevelopment, 100);

        using (var db = new ProjectDbContext(_options))
        {
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

        var activityId = Guid.NewGuid();

        // Run 1: First contribution (20)
        using (var db = new ProjectDbContext(_options))
        {
            var service = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var res1 = await service.RecordContributionAsync(goal.Id, activityId, 20);

            Assert.True(res1.Success);
            Assert.False(res1.IsDuplicateContribution);
            Assert.Equal(0.2f, res1.NewProgress);
        }

        // Run 2: Exact same (GoalId, ActivityId) duplicate attempt (20)
        using (var db = new ProjectDbContext(_options))
        {
            var service = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var res2 = await service.RecordContributionAsync(goal.Id, activityId, 20);

            Assert.True(res2.Success);
            Assert.True(res2.IsDuplicateContribution); // Duplicate suppressed!
            Assert.Equal(0.2f, res2.NewProgress);      // Does not double count!
        }

        // Assert DB Invariant: Exactly 1 contribution record in DB and goal value remains 20
        using (var db = new ProjectDbContext(_options))
        {
            var count = await db.GoalActivityContributions.CountAsync(c => c.GoalId == goal.Id && c.ActivityId == activityId);
            Assert.Equal(1, count);

            var savedGoal = await db.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(20, savedGoal.CurrentValue);
            Assert.Equal(0.2f, savedGoal.Progress);
        }
    }
}
