using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Contracts.Goals;
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
    private readonly DbContextOptions<CoreDbContext> _options;

    public ActivityExecutionIntegrationTests()
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
    public async Task FullAutonomousChain_EndToEnd_FromDecisionToMilestoneToVisualMoment()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Chef", "http://avatar.png", "Passionate culinary master", "Hello", "SliceOfLife", worldDescription: "Royal Kitchen")
        {
            Id = charId
        };

        var goal = new CharacterGoal(charId, "Become Master Chef", CharacterGoalType.Career, 50);
        var m1 = goal.AddMilestone("Learn Knife Skills", 1, 10);
        var m2 = goal.AddMilestone("Master Sauces", 2, 40);

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

        var decisionService = new AutonomousDecisionService(NullLogger<AutonomousDecisionService>.Instance);
        var now = new DateTime(2026, 8, 28, 18, 30, 0, DateTimeKind.Utc); // Evening
        var timeBucket = "2026-08-28T18:00";

        var decisionReq = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: now,
            CurrentLocation: "Royal Kitchen",
            TimeBucket: timeBucket,
            PersonalityPrompt: "Passionate culinary chef",
            Goals: new[]
            {
                new CharacterGoalSnapshot(
                    GoalId: goal.Id,
                    CharacterId: charId,
                    Title: goal.Title,
                    GoalType: goal.GoalType,
                    Priority: CharacterGoalPriority.High,
                    Status: CharacterGoalStatus.Active,
                    Progress: 0f,
                    CurrentValue: 0,
                    TargetValue: 50,
                    CurrentMilestone: "Learn Knife Skills",
                    MilestoneProgress: 0f
                )
            },
            StateSnapshot: new CharacterStateSnapshot(energy: 80, hunger: 70, socialNeed: 20, stress: 20),
            SceneRevision: 2
        );

        // 1. Autonomous Decision
        var decision = await decisionService.DecideNextActionAsync(decisionReq);

        Assert.Equal(AutonomousDecisionAction.PerformActivity, decision.Action);
        Assert.NotNull(decision.Candidate);
        Assert.Equal(CharacterActivityType.Cooking, decision.Candidate.ActivityType);
        Assert.Equal(goal.Id, decision.Candidate.GoalId);

        // 2. Atomic Execution
        using (var db = new CoreDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);

            var execRequest = new ActivityExecutionRequest(
                Character: character,
                Candidate: decision.Candidate,
                CurrentTime: now,
                TimeBucket: timeBucket,
                CurrentState: new CharacterStateSnapshot(energy: 80, hunger: 70, socialNeed: 20, stress: 20),
                SceneRevision: 2
            );

            var execResult = await execService.ExecuteActivityAsync(execRequest);

            Assert.True(execResult.Success);
            Assert.False(execResult.IsDuplicateSuppressed);
            Assert.NotNull(execResult.Activity);
            Assert.NotNull(execResult.NewState);
            Assert.NotNull(execResult.GoalResult);

            // Assert State Consequences applied
            Assert.Equal(70, execResult.NewState.Energy);   // 80 - 10
            Assert.Equal(45, execResult.NewState.Hunger);   // 70 - 25
            Assert.Equal(CharacterMood.Happy, execResult.NewState.Mood);

            // Assert Goal & Milestone Progression
            Assert.True(execResult.GoalResult.Success);
            Assert.True(execResult.GoalResult.MilestoneCompleted); // M1 achieved!
            Assert.False(execResult.GoalResult.GoalCompleted);

            // Assert Visual Moment Triggered & Scene Composed
            Assert.True(execResult.VisualMomentCreated);
            Assert.NotNull(execResult.SceneIntentId);
            Assert.NotNull(execResult.SceneSpecificationId);
        }

        // 3. Assert Authoritative Database Invariants
        using (var db = new CoreDbContext(_options))
        {
            var savedActivity = await db.CharacterActivities.FirstAsync(a => a.CharacterId == charId && a.TimeBucket == timeBucket);
            Assert.Equal(CharacterActivityType.Cooking, savedActivity.ActivityType);
            Assert.Equal(CharacterActivityStatus.Completed, savedActivity.Status);

            var savedGoal = await db.CharacterGoals.Include(g => g.Milestones).FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(22.5, savedGoal.CurrentValue); // 45 min / 2 = 22.5
            var savedM1 = savedGoal.Milestones.First(m => m.Order == 1);
            var savedM2 = savedGoal.Milestones.First(m => m.Order == 2);

            Assert.Equal(CharacterGoalMilestoneStatus.Completed, savedM1.Status);
            Assert.Equal(10, savedM1.CurrentValue);
            Assert.Equal(CharacterGoalMilestoneStatus.Active, savedM2.Status);
            Assert.Equal(12.5, savedM2.CurrentValue);

            var spec = await db.SceneSpecifications.FindAsync(db.SceneSpecifications.First().Id);
            Assert.NotNull(spec);
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

        using (var db = new CoreDbContext(_options))
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
        using (var db = new CoreDbContext(_options))
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
        using (var db = new CoreDbContext(_options))
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
        using (var db = new CoreDbContext(_options))
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
