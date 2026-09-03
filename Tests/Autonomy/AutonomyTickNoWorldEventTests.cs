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

public sealed class AutonomyTickNoWorldEventTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public AutonomyTickNoWorldEventTests()
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
    public async Task NoWorldEvent_StillExecutesAutonomousDecision_AndCompletesTick()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var goal = new CharacterGoal(charId, "Master Alchemical Research", CharacterGoalType.SkillDevelopment, 100);

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

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

            var executionId = Guid.NewGuid();
            var request = new AutonomyTickRequest(
                CharacterId: charId,
                ExecutionId: executionId,
                TimeBucket: "2026-08-28T19:00",
                CurrentTime: DateTime.UtcNow,
                WorldEventId: null // No world event
            );

            var result = await orchestrator.ExecuteTickAsync(request);

            Assert.True(result.Success);
            Assert.False(result.IsDuplicateSuppressed);
            Assert.Equal(executionId, result.ExecutionId);
            Assert.NotNull(result.Tick);
            Assert.Equal(AutonomyTickStatus.Completed, result.Tick.Status);
            Assert.Null(result.ReactionResult); // No reaction executed
            Assert.NotNull(result.ActivityResult);
            Assert.True(result.ActivityResult.Success);
        }
    }
}
