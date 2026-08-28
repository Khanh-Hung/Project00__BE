using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services.Goals;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class PostgresGoalConstraintTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public PostgresGoalConstraintTests()
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

    [Theory]
    [InlineData("23505: duplicate key value violates unique constraint \"IX_GoalActivityContributions_GoalId_ActivityId\"", true)]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: GoalActivityContributions.GoalId, GoalActivityContributions.ActivityId'.", true)]
    [InlineData("duplicate key error on IX_GoalActivityContributions_GoalId_ActivityId", true)]
    [InlineData("23503: insert or update on table \"GoalActivityContributions\" violates foreign key constraint", false)]
    [InlineData("23502: null value in column \"GoalId\" of relation \"GoalActivityContributions\" violates not-null constraint", false)]
    [InlineData("Connection refused: database server down", false)]
    [InlineData("40001: could not serialize access due to concurrent update", false)]
    public void IsDuplicateContributionViolation_AccuratelyClassifies_PostgresAndSqliteErrors(string errorMessage, bool expectedIsUniqueViolation)
    {
        var innerEx = new Exception(errorMessage);
        var dbUpdateEx = new DbUpdateException(errorMessage, innerEx);

        var result = GoalProgressService.IsDuplicateContributionViolation(dbUpdateEx);

        Assert.Equal(expectedIsUniqueViolation, result);
    }

    [Fact]
    public async Task NonUniqueDbUpdateException_IsRethrown_AndNotSwallowed()
    {
        var goalId = Guid.NewGuid();
        var activityId = Guid.NewGuid();

        var brokenConnection = new SqliteConnection("Filename=nonexistent_broken_goals.db");
        var brokenOptions = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(brokenConnection)
            .Options;

        using var brokenDb = new ProjectDbContext(brokenOptions);
        var service = new GoalProgressService(brokenDb, NullLogger<GoalProgressService>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => service.RecordContributionAsync(goalId, activityId, 10));
    }

    [Fact]
    public async Task DuplicateContributions_IdempotentlySuppressed_AndDoesNotDoubleCount()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(charId, "Master Archery", CharacterGoalType.SkillDevelopment, 100);

        using (var db = new ProjectDbContext(_options))
        {
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

        var activityId = Guid.NewGuid();

        // 1. Initial contribution
        using (var db1 = new ProjectDbContext(_options))
        {
            var service1 = new GoalProgressService(db1, NullLogger<GoalProgressService>.Instance);
            var res1 = await service1.RecordContributionAsync(goal.Id, activityId, 10);
            Assert.True(res1.Success);
            Assert.False(res1.IsDuplicateContribution);
            Assert.Equal(0.1f, res1.NewProgress);
        }

        // 2. Duplicate contribution attempt (pre-check path)
        using (var db2 = new ProjectDbContext(_options))
        {
            var service2 = new GoalProgressService(db2, NullLogger<GoalProgressService>.Instance);
            var res2 = await service2.RecordContributionAsync(goal.Id, activityId, 10);
            Assert.True(res2.Success);
            Assert.True(res2.IsDuplicateContribution);
            Assert.Equal(0.1f, res2.NewProgress);
        }

        // Verify database invariants: exactly 1 contribution row and exactly 10 units of progress
        using (var db = new ProjectDbContext(_options))
        {
            var contributionCount = await db.GoalActivityContributions
                .CountAsync(c => c.GoalId == goal.Id && c.ActivityId == activityId);
            Assert.Equal(1, contributionCount);

            var savedGoal = await db.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(10, savedGoal.CurrentValue);
            Assert.Equal(0.1f, savedGoal.Progress);
        }
    }

    [Fact]
    public void ModelMetadata_VerifiesUniqueContributionIndex_AndGoalIndexes()
    {
        using var db = new ProjectDbContext(_options);

        // 1. Verify Unique Index on GoalActivityContribution (GoalId, ActivityId)
        var contribEntity = db.Model.FindEntityType(typeof(GoalActivityContribution));
        Assert.NotNull(contribEntity);

        var contribIndex = contribEntity.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "GoalId", "ActivityId" }));
        Assert.NotNull(contribIndex);
        Assert.True(contribIndex.IsUnique);

        // 2. Verify CharacterGoal indexes (CharacterId, Status), (CharacterId, Priority), and (CharacterId, CreatedAt)
        var goalEntity = db.Model.FindEntityType(typeof(CharacterGoal));
        Assert.NotNull(goalEntity);

        var statusIndex = goalEntity.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "CharacterId", "Status" }));
        Assert.NotNull(statusIndex);

        var priorityIndex = goalEntity.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "CharacterId", "Priority" }));
        Assert.NotNull(priorityIndex);

        var createdAtIndex = goalEntity.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "CharacterId", "CreatedAt" }));
        Assert.NotNull(createdAtIndex);

        // 3. Verify Milestone Index (GoalId, Order)
        var milestoneEntity = db.Model.FindEntityType(typeof(CharacterGoalMilestone));
        Assert.NotNull(milestoneEntity);

        var orderIndex = milestoneEntity.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "GoalId", "Order" }));
        Assert.NotNull(orderIndex);
    }
}
