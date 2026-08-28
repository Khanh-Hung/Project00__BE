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

public sealed class AutonomyTickHappyPathTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public AutonomyTickHappyPathTests()
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
    public async Task HappyPath_WakesCharacter_ProcessesReaction_DecidesAction_ExecutesActivity_CompletesTick()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var goal = new CharacterGoal(charId, "Master Alchemical Research", CharacterGoalType.SkillDevelopment, 100);
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Great work on the research!");

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.CharacterWorldEvents.AddAsync(worldEvent);
            await db.SaveChangesAsync();
        }

        using (var db = new ProjectDbContext(_options))
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

            var executionId = Guid.NewGuid();
            var request = new AutonomyTickRequest(
                CharacterId: charId,
                ExecutionId: executionId,
                TimeBucket: "2026-08-28T14:00",
                CurrentTime: new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc),
                WorldEventId: worldEvent.Id,
                CorrelationId: "corr-happy-001"
            );

            var result = await orchestrator.ExecuteTickAsync(request);

            Assert.True(result.Success);
            Assert.False(result.IsDuplicateSuppressed);
            Assert.Equal(executionId, result.ExecutionId);
            Assert.NotNull(result.Tick);
            Assert.Equal(AutonomyTickStatus.Completed, result.Tick.Status);
            Assert.NotNull(result.Tick.CompletedAt);
            Assert.Null(result.Tick.FailedAt);
            Assert.Null(result.Tick.ErrorMessage);
            Assert.NotNull(result.ReactionResult);
            Assert.NotNull(result.ActivityResult);
            Assert.True(result.ActivityResult.Success);
        }

        // Verify DB persistence
        using (var db = new ProjectDbContext(_options))
        {
            var persistedTick = await db.CharacterAutonomyTicks.FirstOrDefaultAsync(t => t.CharacterId == charId);
            Assert.NotNull(persistedTick);
            Assert.Equal(AutonomyTickStatus.Completed, persistedTick.Status);
            Assert.NotNull(persistedTick.ActivityId);
            Assert.NotNull(persistedTick.ReactionId);

            var activityCount = await db.CharacterActivities.CountAsync(a => a.CharacterId == charId);
            Assert.Equal(1, activityCount);

            var reactionCount = await db.CharacterWorldEventReactions.CountAsync(r => r.CharacterId == charId);
            Assert.Equal(1, reactionCount);
        }
    }
}
