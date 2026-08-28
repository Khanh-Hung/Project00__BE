using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

        // 2. Verify CharacterGoal indexes (CharacterId, Status) and (CharacterId, Priority)
        var goalEntity = db.Model.FindEntityType(typeof(CharacterGoal));
        Assert.NotNull(goalEntity);

        var statusIndex = goalEntity.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "CharacterId", "Status" }));
        Assert.NotNull(statusIndex);

        var priorityIndex = goalEntity.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "CharacterId", "Priority" }));
        Assert.NotNull(priorityIndex);

        // 3. Verify Milestone Index (GoalId, Order)
        var milestoneEntity = db.Model.FindEntityType(typeof(CharacterGoalMilestone));
        Assert.NotNull(milestoneEntity);

        var orderIndex = milestoneEntity.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "GoalId", "Order" }));
        Assert.NotNull(orderIndex);
    }
}
