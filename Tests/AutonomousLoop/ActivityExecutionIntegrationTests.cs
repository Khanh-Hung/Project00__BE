using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
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

public sealed class ActivityExecutionIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public ActivityExecutionIntegrationTests()
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
    public async Task ExecuteActivity_PersistsActivity_MutatesState_AndCascadesGoalProgress()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Alchemist", "http://avatar.png", "Scholar alchemist", "Hello", "Anime", worldDescription: "Arcane Lab")
        {
            Id = charId
        };

        var goal = new CharacterGoal(charId, "Master Arcane Alchemy", CharacterGoalType.SkillDevelopment, 50);
        var m1 = goal.AddMilestone("Basic Potions", 1, 20);
        var m2 = goal.AddMilestone("Advanced Elixirs", 2, 30);

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Cooking,
            Location: "Arcane Kitchen",
            Reason: "Brewing stamina potions for alchemy milestone",
            Priority: ActivityPriority.Normal,
            DurationMinutes: 40, // 40 min / 2 = 20 contribution
            ShouldCreateVisualMoment: false,
            Confidence: 0.95f,
            ActionHint: "stirring bubbling cauldron",
            PoseHint: "standing attentively",
            OutfitHint: null,
            EnvironmentHint: null,
            DecisionFingerprint: "test-fingerprint-001",
            GoalId: goal.Id,
            GoalTitle: goal.Title,
            GoalRelevance: 0.8f,
            GoalReason: "Cooking aligns with alchemy brewing"
        );

        var initialState = new CharacterStateSnapshot(energy: 80, hunger: 60, socialNeed: 30, stress: 30);
        var now = new DateTime(2026, 8, 28, 11, 0, 0, DateTimeKind.Utc);
        var timeBucket = "2026-08-28T11:00";

        var execRequest = new ActivityExecutionRequest(
            Character: character,
            Candidate: candidate,
            CurrentTime: now,
            TimeBucket: timeBucket,
            CurrentState: initialState,
            SceneRevision: 1
        );

        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);

            var result = await execService.ExecuteActivityAsync(execRequest);

            Assert.True(result.Success);
            Assert.False(result.IsDuplicateSuppressed);
            Assert.NotNull(result.Activity);
            Assert.NotNull(result.NewState);
            Assert.NotNull(result.GoalResult);

            // Verify State Deltas applied
            Assert.Equal(70, result.NewState.Energy); // 80 - 10
            Assert.Equal(35, result.NewState.Hunger); // 60 - 25
            Assert.Equal(CharacterMood.Happy, result.NewState.Mood);

            // Verify Goal Progression
            Assert.True(result.GoalResult.Success);
            Assert.Equal(0.4f, result.GoalResult.NewProgress); // 20 / 50 = 0.4
            Assert.True(result.GoalResult.MilestoneCompleted); // M1 achieved
        }

        // Verify DB State
        using (var db = new ProjectDbContext(_options))
        {
            var savedActivity = await db.CharacterActivities.FirstAsync(a => a.CharacterId == charId && a.TimeBucket == timeBucket);
            Assert.Equal(CharacterActivityType.Cooking, savedActivity.ActivityType);
            Assert.Equal(CharacterActivityStatus.Completed, savedActivity.Status);

            var savedGoal = await db.CharacterGoals.Include(g => g.Milestones).FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(20, savedGoal.CurrentValue);
            var savedM1 = savedGoal.Milestones.First(m => m.Order == 1);
            var savedM2 = savedGoal.Milestones.First(m => m.Order == 2);
            Assert.Equal(CharacterGoalMilestoneStatus.Completed, savedM1.Status);
            Assert.Equal(CharacterGoalMilestoneStatus.Active, savedM2.Status);
        }
    }

    [Fact]
    public async Task ExecuteActivity_DuplicateTimeBucket_SuppressedWithoutDuplicateGoalContribution()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Alchemist", "http://avatar.png", "Scholar alchemist", "Hello", "Anime")
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

        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Reading,
            Location: "Library",
            Reason: "Reading research scrolls",
            Priority: ActivityPriority.Normal,
            DurationMinutes: 30, // 15 contribution
            ShouldCreateVisualMoment: false,
            Confidence: 0.95f,
            ActionHint: "reading",
            PoseHint: "seated",
            OutfitHint: null,
            EnvironmentHint: null,
            DecisionFingerprint: "fingerprint-dup-001",
            GoalId: goal.Id
        );

        var now = new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc);
        var timeBucket = "2026-08-28T14:00";

        var execRequest = new ActivityExecutionRequest(
            Character: character,
            Candidate: candidate,
            CurrentTime: now,
            TimeBucket: timeBucket,
            CurrentState: CharacterStateSnapshot.CreateDefault()
        );

        // Run 1: Succeeds
        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);

            var res1 = await execService.ExecuteActivityAsync(execRequest);
            Assert.True(res1.Success);
            Assert.False(res1.IsDuplicateSuppressed);
        }

        // Run 2: Exact same Character & TimeBucket -> Suppressed!
        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);

            var res2 = await execService.ExecuteActivityAsync(execRequest);
            Assert.True(res2.Success);
            Assert.True(res2.IsDuplicateSuppressed);
        }

        // Assert DB Invariant: Exactly 1 activity record, exactly 1 contribution, exactly 15 units of progress
        using (var db = new ProjectDbContext(_options))
        {
            var actCount = await db.CharacterActivities.CountAsync(a => a.CharacterId == charId && a.TimeBucket == timeBucket);
            Assert.Equal(1, actCount);

            var contribCount = await db.GoalActivityContributions.CountAsync(c => c.GoalId == goal.Id);
            Assert.Equal(1, contribCount);

            var savedGoal = await db.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(15, savedGoal.CurrentValue);
        }
    }
}
