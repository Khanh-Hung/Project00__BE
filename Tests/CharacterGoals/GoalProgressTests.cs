using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services.Goals;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class GoalProgressTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public GoalProgressTests()
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
    public async Task RecordContribution_UpdatesGoalProgress_AndCompletesGoalAtomically()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "Build Observatory", CharacterGoalType.Creative, 50);
        var m1 = goal.AddMilestone("Foundation", 1, 25);
        var m2 = goal.AddMilestone("Telescope Mount", 2, 25);

        using (var db = new ProjectDbContext(_options))
        {
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

        var activityId1 = Guid.NewGuid();
        var activityId2 = Guid.NewGuid();

        // 1. First contribution (25)
        using (var db = new ProjectDbContext(_options))
        {
            var service = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var res1 = await service.RecordContributionAsync(goal.Id, activityId1, 25);

            Assert.True(res1.Success);
            Assert.False(res1.IsDuplicateContribution);
            Assert.Equal(0f, res1.PreviousProgress);
            Assert.Equal(0.5f, res1.NewProgress);
            Assert.True(res1.MilestoneCompleted);
            Assert.False(res1.GoalCompleted);
        }

        // 2. Second contribution (25) -> Completes Goal
        using (var db = new ProjectDbContext(_options))
        {
            var service = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var res2 = await service.RecordContributionAsync(goal.Id, activityId2, 25);

            Assert.True(res2.Success);
            Assert.False(res2.IsDuplicateContribution);
            Assert.Equal(0.5f, res2.PreviousProgress);
            Assert.Equal(1.0f, res2.NewProgress);
            Assert.True(res2.MilestoneCompleted);
            Assert.True(res2.GoalCompleted);
        }

        // Verify DB State
        using (var db = new ProjectDbContext(_options))
        {
            var savedGoal = await db.CharacterGoals.Include(g => g.Milestones).FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(CharacterGoalStatus.Completed, savedGoal.Status);
            Assert.Equal(50, savedGoal.CurrentValue);
            Assert.Equal(1.0f, savedGoal.Progress);
            Assert.NotNull(savedGoal.CompletedAt);

            var contributions = await db.GoalActivityContributions.Where(c => c.GoalId == goal.Id).ToListAsync();
            Assert.Equal(2, contributions.Count);
        }
    }

    [Fact]
    public async Task OvershootProgress_ClampsToTargetAndCompletes()
    {
        var goal = new CharacterGoal(Guid.NewGuid(), "Learn Basic French", CharacterGoalType.SkillDevelopment, 20);

        using (var db = new ProjectDbContext(_options))
        {
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

        using (var db = new ProjectDbContext(_options))
        {
            var service = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var res = await service.RecordContributionAsync(goal.Id, Guid.NewGuid(), 50);

            Assert.True(res.Success);
            Assert.Equal(1.0f, res.NewProgress);
            Assert.True(res.GoalCompleted);
        }

        using (var db = new ProjectDbContext(_options))
        {
            var saved = await db.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(CharacterGoalStatus.Completed, saved.Status);
            Assert.Equal(50, saved.CurrentValue);
            Assert.Equal(1.0f, saved.Progress);
        }
    }
}
