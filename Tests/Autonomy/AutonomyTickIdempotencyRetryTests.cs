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

public sealed class AutonomyTickIdempotencyRetryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public AutonomyTickIdempotencyRetryTests()
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
    public async Task SequentialRetry_WithSameCharacterAndTimeBucket_ReturnsDuplicateSuppressed_WithoutDuplicateEffects()
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

        var timeBucket = "2026-08-28T17:00";
        var executionId = Guid.NewGuid();
        var request = new AutonomyTickRequest(
            CharacterId: charId,
            ExecutionId: executionId,
            TimeBucket: timeBucket,
            CurrentTime: new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc),
            WorldEventId: worldEvent.Id,
            CorrelationId: "corr-seq-001"
        );

        // Attempt 1: First invocation -> Success with full side-effects
        using (var db = new CoreDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var contextLoader = new AutonomousCharacterContextLoader(db, stateReader, new Infrastructure.Services.State.CharacterStateService(db, new Infrastructure.Services.State.CharacterStateTransitionService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateTransitionService>.Instance), new Domain.Policies.CharacterStateEvolutionPolicy(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateService>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<AutonomousCharacterContextLoader>.Instance);
            var decisionService = new AutonomousDecisionService(NullLogger<AutonomousDecisionService>.Instance);
            var activityExecService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, new Infrastructure.Services.State.CharacterStateTransitionService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateTransitionService>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, activityExecService, fakePipeline, stateReader, new Infrastructure.Services.State.CharacterStateTransitionService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateTransitionService>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<CharacterReactionExecutionService>.Instance);

            var orchestrator = new AutonomousCharacterLifecycleOrchestrator(
                db,
                contextLoader,
                decisionService,
                activityExecService,
                reactionService,
                NullLogger<AutonomousCharacterLifecycleOrchestrator>.Instance
            );

            var res1 = await orchestrator.ExecuteTickAsync(request);

            Assert.True(res1.Success);
            Assert.False(res1.IsDuplicateSuppressed);
            Assert.Equal(executionId, res1.ExecutionId);
            Assert.NotNull(res1.Tick);
            Assert.NotNull(res1.ReactionResult);
            Assert.NotNull(res1.ActivityResult);
            Assert.True(res1.ActivityResult.Success);
        }

        // Attempt 2: Sequential Retry with same TimeBucket -> Duplicate Suppressed with zero duplicate effects
        using (var db = new CoreDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var contextLoader = new AutonomousCharacterContextLoader(db, stateReader, new Infrastructure.Services.State.CharacterStateService(db, new Infrastructure.Services.State.CharacterStateTransitionService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateTransitionService>.Instance), new Domain.Policies.CharacterStateEvolutionPolicy(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateService>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<AutonomousCharacterContextLoader>.Instance);
            var decisionService = new AutonomousDecisionService(NullLogger<AutonomousDecisionService>.Instance);
            var activityExecService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, new Infrastructure.Services.State.CharacterStateTransitionService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateTransitionService>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, activityExecService, fakePipeline, stateReader, new Infrastructure.Services.State.CharacterStateTransitionService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateTransitionService>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<CharacterReactionExecutionService>.Instance);

            var orchestrator = new AutonomousCharacterLifecycleOrchestrator(
                db,
                contextLoader,
                decisionService,
                activityExecService,
                reactionService,
                NullLogger<AutonomousCharacterLifecycleOrchestrator>.Instance
            );

            var res2 = await orchestrator.ExecuteTickAsync(request);

            Assert.True(res2.Success);
            Assert.True(res2.IsDuplicateSuppressed);
            Assert.Equal(executionId, res2.ExecutionId);
            Assert.Null(res2.Tick);
            Assert.Null(res2.ReactionResult);
            Assert.Null(res2.ActivityResult);
        }

        // Strict Invariant verification: Exactly 1 row in CharacterAutonomyTicks, 1 Reaction, 1 Activity, 1 Memory, 1 SceneSpecification (from reaction)
        using (var db = new CoreDbContext(_options))
        {
            var tickCount = await db.CharacterAutonomyTicks.CountAsync(t => t.CharacterId == charId && t.TimeBucket == timeBucket);
            Assert.Equal(1, tickCount);

            var reactionCount = await db.CharacterWorldEventReactions.CountAsync(r => r.CharacterId == charId);
            Assert.Equal(1, reactionCount);

            var activityCount = await db.CharacterActivities.CountAsync(a => a.CharacterId == charId);
            Assert.Equal(1, activityCount);

            var memCount = await db.CharacterMemories.CountAsync(m => m.CharacterId == charId);
            Assert.Equal(1, memCount);

            var dbGoal = await db.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(32.0, dbGoal.CurrentValue); // Exact 32.0 invariant!

            var specCount = await db.SceneSpecifications.CountAsync(s => s.CharacterId == charId);
            Assert.Equal(1, specCount);
        }
    }
}
