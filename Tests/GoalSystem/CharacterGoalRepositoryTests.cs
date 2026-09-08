using System;
using System.Linq;
using System.Threading.Tasks;
using Application.Contracts.Goals;
using Application.Abstractions.Time;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services.Goals;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
            DateTimeOffset.UtcNow,
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
            Assert.Equal(0.25f, retrieved.Progress);
            Assert.Equal(25, retrieved.ProgressPercentage);
        }
    }

    [Fact]
    public async Task Repository_CanRetrieveActiveGoals()
    {
        var charId = Guid.NewGuid();

        var activeGoal = new CharacterGoal(charId, "ActiveGoal", DateTimeOffset.UtcNow, initialStatus: CharacterGoalStatus.Active);
        var completedGoal = new CharacterGoal(charId, "CompletedGoal", DateTimeOffset.UtcNow, initialStatus: CharacterGoalStatus.Active);
        completedGoal.Complete(DateTimeOffset.UtcNow);
        var cancelledGoal = new CharacterGoal(charId, "CancelledGoal", DateTimeOffset.UtcNow, initialStatus: CharacterGoalStatus.Active);
        cancelledGoal.Cancel(DateTimeOffset.UtcNow);

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

        var goalA = new CharacterGoal(charA, "GoalA", DateTimeOffset.UtcNow, initialStatus: CharacterGoalStatus.Active);
        var goalB = new CharacterGoal(charB, "GoalB", DateTimeOffset.UtcNow, initialStatus: CharacterGoalStatus.Active);

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

        // Goal A: Priority = Normal (1), Progress = 20%
        var goalA = new CharacterGoal(
            charId, "GoalA",
            DateTimeOffset.UtcNow,
            priority: CharacterGoalPriority.Normal,
            initialStatus: CharacterGoalStatus.Active,
            initialProgress: 20);

        // Goal B: Priority = High (2), Progress = 10%
        var goalB = new CharacterGoal(
            charId, "GoalB",
            DateTimeOffset.UtcNow,
            priority: CharacterGoalPriority.High,
            initialStatus: CharacterGoalStatus.Active,
            initialProgress: 10);

        // Goal C: Priority = High (2), Progress = 40%
        var goalC = new CharacterGoal(
            charId, "GoalC",
            DateTimeOffset.UtcNow,
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
        var goal1 = new CharacterGoal(charId, "BuildRelationship", DateTimeOffset.UtcNow, initialStatus: CharacterGoalStatus.Active);
        var goal2 = new CharacterGoal(charId, "BuildRelationship", DateTimeOffset.UtcNow, initialStatus: CharacterGoalStatus.Active);

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

    [Fact]
    public async Task GoalUniqueConstraintViolation_IsRecognized()
    {
        var charId = Guid.NewGuid();
        var goal1 = new CharacterGoal(charId, "BuildRelationship", DateTimeOffset.UtcNow, initialStatus: CharacterGoalStatus.Active);
        var goal2 = new CharacterGoal(charId, "BuildRelationship", DateTimeOffset.UtcNow, initialStatus: CharacterGoalStatus.Active);

        using (var db1 = new CoreDbContext(_options))
        {
            await db1.CharacterGoals.AddAsync(goal1);
            await db1.SaveChangesAsync();
        }

        using (var db2 = new CoreDbContext(_options))
        {
            await db2.CharacterGoals.AddAsync(goal2);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());

            // Must accurately recognize unique constraint violation on active character goals
            Assert.True(CharacterGoalService.IsActiveGoalUniqueViolation(ex));
        }
    }

    [Fact]
    public void UnrelatedDbUpdateException_IsNotRecognized()
    {
        // Unrelated exception with arbitrary text that happens to contain "19" or "error"
        var unrelatedInner = new Exception("Foreign key constraint failed at row 19 in table Users");
        var unrelatedEx = new DbUpdateException("An error occurred saving entities", unrelatedInner);

        Assert.False(CharacterGoalService.IsActiveGoalUniqueViolation(unrelatedEx));
        Assert.False(CharacterGoalService.IsGoalProgressUniqueViolation(unrelatedEx));
    }

    [Fact]
    public async Task ConcurrentGoalCreation_DeterministicRace_OneWinnerOneReloadsWinner()
    {
        var charId = Guid.NewGuid();
        var goalKey = "BuildRelationship";
        var now = DateTimeOffset.UtcNow;

        // Use a SaveChangesInterceptor to deterministically suspend Worker B right before save
        var interceptorB = new ConcurrencyBarrierInterceptor();
        var optionsB = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptorB)
            .Options;

        using var dbA = new CoreDbContext(_options);
        using var dbB = new CoreDbContext(optionsB);

        var repoA = new CharacterGoalRepository(dbA);
        var repoB = new CharacterGoalRepository(dbB);

        var policy = new CharacterGoalPolicy();

        var serviceA = new CharacterGoalService(dbA, repoA, policy, NullLogger<CharacterGoalService>.Instance);
        var serviceB = new CharacterGoalService(dbB, repoB, policy, NullLogger<CharacterGoalService>.Instance);

        var desire = new CharacterDesire(
            DesireType.NeedSocialConnection,
            0.9,
            DesireSource.SocialNeed,
            new CharacterMotivation(MotivationType.ConnectionDriven, 0.9, DesireSource.SocialNeed));
        var desireEval = new CharacterDesireEvaluation(charId, 1, new[] { desire }, desire);

        // 1. Worker B begins execution and checks existing active goals (sees 0), generates new goal, and suspends right before SavingChanges
        var taskB = Task.Run(async () => await serviceB.GetOrSelectActiveGoalAsync(charId, desireEval, now));
        await interceptorB.WaitForBeforeSaveAsync();

        // 2. Worker A executes while Worker B is suspended, checks existing active goals (sees 0), inserts, and commits winner to DB
        var goalA = await serviceA.GetOrSelectActiveGoalAsync(charId, desireEval, now);
        Assert.NotNull(goalA);

        // 3. Worker B is now released to attempt its commit
        // Database enforces unique active constraint -> Worker B catches exception, reloads winner from DB
        interceptorB.Release();
        var goalB = await taskB;
        Assert.NotNull(goalB);

        // Both workers must agree on the same winner goal instance
        Assert.Equal(goalA.Id, goalB.Id);
        Assert.Equal(goalKey, goalA.Title);

        // Verify exactly 1 active goal exists in the DB for this character
        using var verifyDb = new CoreDbContext(_options);
        var activeInDb = await verifyDb.CharacterGoals
            .Where(g => g.CharacterId == charId && g.Status == CharacterGoalStatus.Active)
            .ToListAsync();

        Assert.Single(activeInDb);
        Assert.Equal(goalA.Id, activeInDb[0].Id);
    }
}
