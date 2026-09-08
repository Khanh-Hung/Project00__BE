using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tests.GoalSystem;

public sealed class CharacterGoalRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterGoalRepositoryTests()
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
    public async Task Repository_CanPersistGoal()
    {
        var charId = Guid.NewGuid();
        var goal = new CharacterGoal(
            charId,
            "MasterCooking",
            CharacterGoalType.SkillDevelopment,
            targetValue: 100,
            priority: CharacterGoalPriority.High,
            description: "Learn gourmet cooking recipes",
            initialStatus: CharacterGoalStatus.Active,
            initialProgress: 25);

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterGoalRepository(db);
            await repo.AddAsync(goal);
        }

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterGoalRepository(db);
            var retrieved = await repo.GetByIdAsync(goal.Id);

            Assert.NotNull(retrieved);
            Assert.Equal(goal.Id, retrieved.Id);
            Assert.Equal(charId, retrieved.CharacterId);
            Assert.Equal("MasterCooking", retrieved.Title);
            Assert.Equal(CharacterGoalType.SkillDevelopment, retrieved.GoalType);
            Assert.Equal(CharacterGoalPriority.High, retrieved.Priority);
            Assert.Equal(CharacterGoalStatus.Active, retrieved.Status);
            Assert.Equal(25f, retrieved.Progress);
        }
    }

    [Fact]
    public async Task Repository_CanRetrieveActiveGoals()
    {
        var charId = Guid.NewGuid();

        var activeGoal = new CharacterGoal(charId, "ActiveGoal", initialStatus: CharacterGoalStatus.Active);
        var completedGoal = new CharacterGoal(charId, "CompletedGoal", initialStatus: CharacterGoalStatus.Active);
        completedGoal.Complete();
        var cancelledGoal = new CharacterGoal(charId, "CancelledGoal", initialStatus: CharacterGoalStatus.Active);
        cancelledGoal.Cancel();

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterGoalRepository(db);
            await repo.AddAsync(activeGoal);
            await repo.AddAsync(completedGoal);
            await repo.AddAsync(cancelledGoal);
        }

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterGoalRepository(db);
            var activeList = await repo.GetActiveGoalsAsync(charId);

            Assert.Single(activeList);
            Assert.Equal(activeGoal.Id, activeList[0].Id);
        }
    }

    [Fact]
    public async Task Repository_QueriesAreCharacterScoped()
    {
        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();

        var goalA = new CharacterGoal(charA, "GoalA", initialStatus: CharacterGoalStatus.Active);
        var goalB = new CharacterGoal(charB, "GoalB", initialStatus: CharacterGoalStatus.Active);

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterGoalRepository(db);
            await repo.AddAsync(goalA);
            await repo.AddAsync(goalB);
        }

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterGoalRepository(db);
            var listA = await repo.GetActiveGoalsAsync(charA);
            var listB = await repo.GetActiveGoalsAsync(charB);

            Assert.Single(listA);
            Assert.Equal(goalA.Id, listA[0].Id);

            Assert.Single(listB);
            Assert.Equal(goalB.Id, listB[0].Id);
        }
    }

    [Fact]
    public async Task Repository_DeterministicGoalOrdering()
    {
        var charId = Guid.NewGuid();

        // Goal A: Priority = Normal (1), Progress = 20
        var goalA = new CharacterGoal(
            charId, "GoalA",
            priority: CharacterGoalPriority.Normal,
            initialStatus: CharacterGoalStatus.Active,
            initialProgress: 20);

        // Goal B: Priority = High (2), Progress = 10
        var goalB = new CharacterGoal(
            charId, "GoalB",
            priority: CharacterGoalPriority.High,
            initialStatus: CharacterGoalStatus.Active,
            initialProgress: 10);

        // Goal C: Priority = High (2), Progress = 40
        var goalC = new CharacterGoal(
            charId, "GoalC",
            priority: CharacterGoalPriority.High,
            initialStatus: CharacterGoalStatus.Active,
            initialProgress: 40);

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterGoalRepository(db);
            await repo.AddAsync(goalA);
            await repo.AddAsync(goalB);
            await repo.AddAsync(goalC);
        }

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterGoalRepository(db);
            var goals = await repo.GetActiveGoalsAsync(charId);

            Assert.Equal(3, goals.Count);
            // Ordering: Priority DESC, Progress ASC, CreatedAt ASC, Id ASC
            Assert.Equal(goalB.Id, goals[0].Id); // High priority, lower progress (10)
            Assert.Equal(goalC.Id, goals[1].Id); // High priority, higher progress (40)
            Assert.Equal(goalA.Id, goals[2].Id); // Normal priority

            var highest = await repo.GetHighestPriorityActiveGoalAsync(charId);
            Assert.NotNull(highest);
            Assert.Equal(goalB.Id, highest.Id);
        }
    }

    [Fact]
    public async Task Repository_DuplicateSemanticActiveGoal_IsPreventedOrReused()
    {
        var charId = Guid.NewGuid();
        var goal1 = new CharacterGoal(charId, "BuildRelationship", initialStatus: CharacterGoalStatus.Active);
        var goal2 = new CharacterGoal(charId, "BuildRelationship", initialStatus: CharacterGoalStatus.Active);

        using (var db1 = new CoreDbContext(_options))
        {
            var repo1 = new CharacterGoalRepository(db1);
            await repo1.AddAsync(goal1);
        }

        // Second insert of same semantic active goal for same character must violate unique constraint
        using (var db2 = new CoreDbContext(_options))
        {
            var repo2 = new CharacterGoalRepository(db2);
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => repo2.AddAsync(goal2));
        }
    }
}
