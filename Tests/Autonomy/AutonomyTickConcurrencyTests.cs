using Application.Contracts.Autonomy;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services.Autonomous;
using Infrastructure.Services.Autonomy;
using Infrastructure.Services.Goals;
using Infrastructure.Services.Reactions;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Helpers;
using Xunit;

namespace Tests.Autonomy;

public sealed class AutonomyTickConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public AutonomyTickConcurrencyTests()
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
    public async Task TwoConcurrentWorkers_AttemptingSameCharacterAndTimeBucket_AllowsOneWinnerAndOneSuppression()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.SaveChangesAsync();
        }

        var timeBucket = "2026-08-28T15:00";

        var tasks = Enumerable.Range(1, 2).Select(async i =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var goalService = new GoalProgressService(workerDb, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(workerDb, NullLogger<SceneVisualStateReader>.Instance);
            var contextLoader = new AutonomousCharacterContextLoader(workerDb, stateReader, NullLogger<AutonomousCharacterContextLoader>.Instance);
            var decisionService = new AutonomousDecisionService(NullLogger<AutonomousDecisionService>.Instance);
            var activityExecService = new ActivityExecutionService(workerDb, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(workerDb, goalService, activityExecService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var orchestrator = new AutonomousCharacterLifecycleOrchestrator(
                workerDb,
                contextLoader,
                decisionService,
                activityExecService,
                reactionService,
                NullLogger<AutonomousCharacterLifecycleOrchestrator>.Instance
            );

            var request = new AutonomyTickRequest(
                CharacterId: charId,
                ExecutionId: Guid.NewGuid(), // Distinct ExecutionId per worker
                TimeBucket: timeBucket,
                CurrentTime: DateTime.UtcNow
            );

            return await orchestrator.ExecuteTickAsync(request);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int winners = results.Count(r => r.Success && !r.IsDuplicateSuppressed);
        int suppressed = results.Count(r => r.Success && r.IsDuplicateSuppressed);

        Assert.Equal(1, winners);
        Assert.Equal(1, suppressed);
    }

    [Fact]
    public async Task TwoDifferentTimeBuckets_ExecuteAsTwoIndependentTicks()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.SaveChangesAsync();
        }

        using (var db = new CoreDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var contextLoader = new AutonomousCharacterContextLoader(db, stateReader, NullLogger<AutonomousCharacterContextLoader>.Instance);
            var decisionService = new AutonomousDecisionService(NullLogger<AutonomousDecisionService>.Instance);
            var activityExecService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, activityExecService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var orchestrator = new AutonomousCharacterLifecycleOrchestrator(
                db,
                contextLoader,
                decisionService,
                activityExecService,
                reactionService,
                NullLogger<AutonomousCharacterLifecycleOrchestrator>.Instance
            );

            var res1 = await orchestrator.ExecuteTickAsync(new AutonomyTickRequest(charId, Guid.NewGuid(), "2026-08-28T09:00", DateTime.UtcNow));
            var res2 = await orchestrator.ExecuteTickAsync(new AutonomyTickRequest(charId, Guid.NewGuid(), "2026-08-28T10:00", DateTime.UtcNow));

            Assert.True(res1.Success);
            Assert.False(res1.IsDuplicateSuppressed);
            Assert.True(res2.Success);
            Assert.False(res2.IsDuplicateSuppressed);

            var totalTicks = await db.CharacterAutonomyTicks.CountAsync(t => t.CharacterId == charId);
            Assert.Equal(2, totalTicks);
        }
    }

    [Fact]
    public async Task TenConcurrentWorkers_AttemptingSameCharacterAndTimeBucket_AllowsExactlyOneWinnerAndNineSuppressed_WithZeroDuplicateSideEffects()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar and Arcane Researcher", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var goal = new CharacterGoal(charId, "Master Alchemical Research", CharacterGoalType.SkillDevelopment, 100);
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Great alchemical research discovery!");

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.CharacterWorldEvents.AddAsync(worldEvent);
            await db.SaveChangesAsync();
        }

        var timeBucket = "2026-08-28T16:00";
        var sharedExecutionId = Guid.NewGuid();

        var tasks = Enumerable.Range(1, 10).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var goalService = new GoalProgressService(workerDb, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(workerDb, NullLogger<SceneVisualStateReader>.Instance);
            var contextLoader = new AutonomousCharacterContextLoader(workerDb, stateReader, NullLogger<AutonomousCharacterContextLoader>.Instance);
            var decisionService = new AutonomousDecisionService(NullLogger<AutonomousDecisionService>.Instance);
            var activityExecService = new ActivityExecutionService(workerDb, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(workerDb, goalService, activityExecService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var orchestrator = new AutonomousCharacterLifecycleOrchestrator(
                workerDb,
                contextLoader,
                decisionService,
                activityExecService,
                reactionService,
                NullLogger<AutonomousCharacterLifecycleOrchestrator>.Instance
            );

            var request = new AutonomyTickRequest(
                CharacterId: charId,
                ExecutionId: sharedExecutionId,
                TimeBucket: timeBucket,
                CurrentTime: new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc),
                WorldEventId: worldEvent.Id
            );

            return await orchestrator.ExecuteTickAsync(request);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int winners = results.Count(r => r.Success && !r.IsDuplicateSuppressed);
        int suppressed = results.Count(r => r.Success && r.IsDuplicateSuppressed);

        Assert.Equal(1, winners);
        Assert.Equal(9, suppressed);

        // Strict Invariant Assertions:
        // 1. Exactly 1 row in CharacterAutonomyTicks
        // 2. Exactly 1 row in CharacterWorldEventReactions
        // 3. Exactly 1 row in CharacterActivities
        // 4. Exactly 1 row in CharacterMemories
        // 5. Goal contribution applied exactly once: 30.0 (Reaction) + 2.0 (Activity) = 32.0 (NOT multiplied by 10)
        // 6. Exactly 1 SceneSpecification generated (1 from reaction in the single winning tick, NOT 10 for 10 workers)
        using (var db = new CoreDbContext(_options))
        {
            var tickCount = await db.CharacterAutonomyTicks.CountAsync(t => t.CharacterId == charId && t.TimeBucket == timeBucket);
            Assert.Equal(1, tickCount);

            var reactionCount = await db.CharacterWorldEventReactions.CountAsync(r => r.CharacterId == charId);
            Assert.Equal(1, reactionCount);

            var activityCount = await db.CharacterActivities.CountAsync(a => a.CharacterId == charId);
            Assert.Equal(1, activityCount);

            var memoryCount = await db.CharacterMemories.CountAsync(m => m.CharacterId == charId);
            Assert.Equal(1, memoryCount);

            var dbGoal = await db.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(32.0, dbGoal.CurrentValue); // Exact 32.0 invariant!

            var specCount = await db.SceneSpecifications.CountAsync(s => s.CharacterId == charId);
            Assert.Equal(1, specCount);
        }
    }
}
