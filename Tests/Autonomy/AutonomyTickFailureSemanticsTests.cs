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

public sealed class AutonomyTickFailureSemanticsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public AutonomyTickFailureSemanticsTests()
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
    public async Task FailureDuringExecution_TransitionsTickToFailedStatus_AndPreservesExecutionIdentity()
    {
        var nonExistentCharacterId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var timeBucket = "2026-08-28T18:00";

        using var db = new ProjectDbContext(_options);
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
            CharacterId: nonExistentCharacterId,
            ExecutionId: executionId,
            TimeBucket: timeBucket,
            CurrentTime: DateTime.UtcNow
        );

        var result = await orchestrator.ExecuteTickAsync(request);

        Assert.False(result.Success);
        Assert.False(result.IsDuplicateSuppressed);
        Assert.Equal(executionId, result.ExecutionId);
        Assert.NotNull(result.Tick);
        Assert.Equal(AutonomyTickStatus.Failed, result.Tick.Status);
        Assert.NotNull(result.Tick.FailedAt);
        Assert.Null(result.Tick.CompletedAt);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);

        // Verify DB persistence of Failed state
        var persistedTick = await db.CharacterAutonomyTicks.FirstOrDefaultAsync(t => t.CharacterId == nonExistentCharacterId);
        Assert.NotNull(persistedTick);
        Assert.Equal(AutonomyTickStatus.Failed, persistedTick.Status);
        Assert.NotNull(persistedTick.FailedAt);
    }
}
