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

public sealed class AutonomyTickCorrelationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public AutonomyTickCorrelationTests()
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
    public async Task ExecutionIdAndCorrelationId_PropagatedConsistentlyThroughoutLifecycle()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var callerExecutionId = Guid.NewGuid();
        var correlationId = "corr-lifecycle-777";
        var timeBucket = "2026-08-28T20:00";

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.SaveChangesAsync();
        }

        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var decisionService = new AutonomousDecisionService(NullLogger<AutonomousDecisionService>.Instance);
            var activityExecService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, activityExecService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var orchestrator = new AutonomousCharacterLifecycleOrchestrator(
                db,
                decisionService,
                activityExecService,
                reactionService,
                stateReader,
                NullLogger<AutonomousCharacterLifecycleOrchestrator>.Instance
            );

            var request = new AutonomyTickRequest(
                CharacterId: charId,
                ExecutionId: callerExecutionId,
                TimeBucket: timeBucket,
                CurrentTime: DateTime.UtcNow,
                CorrelationId: correlationId
            );

            var result = await orchestrator.ExecuteTickAsync(request);

            Assert.True(result.Success);
            Assert.Equal(callerExecutionId, result.ExecutionId);
            Assert.NotNull(result.Tick);
            Assert.Equal(callerExecutionId, result.Tick.ExecutionId);
            Assert.Equal(correlationId, result.Tick.CorrelationId);

            if (result.ActivityResult != null)
            {
                Assert.Equal(callerExecutionId, result.ActivityResult.ExecutionId);
            }
        }
    }
}
