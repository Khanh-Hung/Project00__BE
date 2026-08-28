using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.BackgroundJobs;
using Infrastructure.Persistence;
using Infrastructure.Services.Autonomous;
using Infrastructure.Services.Goals;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Helpers;
using Xunit;

namespace Tests.AutonomousLoop;

public sealed class AutonomousConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public AutonomousConcurrencyTests()
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
    public async Task TenConcurrentAutonomousWorkers_AllowsExactlyOneWinner_AndSuppressesNineDuplicates()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Alchemist", "http://avatar.png", "Stoic scholar alchemist", "Hello", "Anime", worldDescription: "Arcane Laboratory")
        {
            Id = charId
        };
        var goal = new CharacterGoal(charId, "Master Arcane Alchemy", CharacterGoalType.SkillDevelopment, 100);

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

        var testTime = new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc);
        var timeBucket = CharacterActivityScheduler.GetTimeBucket(testTime);

        // 10 concurrent autonomous workers trying to process the exact same character simultaneously
        var tasks = Enumerable.Range(1, 10).Select(async workerId =>
        {
            await using var workerDb = new ProjectDbContext(_options);
            var decisionService = new AutonomousDecisionService(NullLogger<AutonomousDecisionService>.Instance);
            var goalService = new GoalProgressService(workerDb, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(workerDb, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(workerDb, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var scheduler = new CharacterActivityScheduler(
                workerDb, decisionService, execService, stateReader, NullLogger<CharacterActivityScheduler>.Instance);

            return await scheduler.ProcessCharacterAsync(character, testTime, timeBucket);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int winners = results.Count(r => r == true);
        int suppressed = results.Count(r => r == false);

        Assert.Equal(1, winners);
        Assert.Equal(9, suppressed);

        // Assert DB Invariant: Exactly 1 activity record exists in DB and exactly 1 goal contribution
        using (var db = new ProjectDbContext(_options))
        {
            var count = await db.CharacterActivities.CountAsync(a => a.CharacterId == charId && a.TimeBucket == timeBucket);
            Assert.Equal(1, count);

            var contribCount = await db.GoalActivityContributions.CountAsync(c => c.GoalId == goal.Id);
            Assert.Equal(1, contribCount);
        }
    }

    [Fact]
    public async Task TenConcurrentWorkers_ExecutingVisualMomentActivity_CreatesExactlyOneVisualMomentAndNineSuppressed()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Explorer", "http://avatar.png", "Brave explorer", "Hello", "Anime", worldDescription: "Ancient Ruins")
        {
            Id = charId
        };

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.SaveChangesAsync();
        }

        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Exploring,
            Location: "Ancient Temple Ruins",
            Reason: "Surveying mysterious ancient inscriptions",
            Priority: ActivityPriority.High,
            DurationMinutes: 60,
            ShouldCreateVisualMoment: true, // High value visual moment!
            Confidence: 0.95f,
            ActionHint: "holding a torch examining glyphs",
            PoseHint: "standing attentively inspecting wall",
            OutfitHint: "Exploration Attire",
            EnvironmentHint: "Atmospheric torchlight",
            DecisionFingerprint: "fingerprint-visual-concurrent-001"
        );

        var testTime = new DateTime(2026, 8, 28, 15, 0, 0, DateTimeKind.Utc);
        var timeBucket = "2026-08-28T15:00";

        // 10 concurrent workers directly dispatching the visual moment execution
        var tasks = Enumerable.Range(1, 10).Select(async workerId =>
        {
            await using var workerDb = new ProjectDbContext(_options);
            var goalService = new GoalProgressService(workerDb, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(workerDb, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(workerDb, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);

            var request = new ActivityExecutionRequest(
                Character: character,
                Candidate: candidate,
                CurrentTime: testTime,
                TimeBucket: timeBucket,
                ExecutionId: Guid.NewGuid(),
                CurrentVisualState: new CharacterVisualState(charId, "Ancient Ruins", sceneRevision: 1),
                CurrentState: CharacterStateSnapshot.CreateDefault(),
                SceneRevision: 1
            );

            return await execService.ExecuteActivityAsync(request);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int winners = results.Count(r => r.Success && !r.IsDuplicateSuppressed && r.VisualMomentCreated);
        int suppressed = results.Count(r => r.Success && r.IsDuplicateSuppressed && !r.VisualMomentCreated);

        Assert.Equal(1, winners);
        Assert.Equal(9, suppressed);

        // Assert Database Authoritative Invariants
        using (var db = new ProjectDbContext(_options))
        {
            // Exactly 1 CharacterActivity row in DB
            var activityCount = await db.CharacterActivities.CountAsync(a => a.CharacterId == charId && a.TimeBucket == timeBucket);
            Assert.Equal(1, activityCount);

            // Exactly 1 SceneSpecification persisted in DB (no duplicate visual generations)
            var specCount = await db.SceneSpecifications.CountAsync(s => s.CharacterId == charId);
            Assert.Equal(1, specCount);
        }
    }
}
