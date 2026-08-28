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

    [Fact]
    public async Task FailedTick_AllowsControlledRetry_AndTransitionsToCompletedUponSuccess()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var timeBucket = "2026-08-28T23:00";

        // Initial setup: Character does not exist yet when initial tick runs
        var executionId1 = Guid.NewGuid();
        var request1 = new AutonomyTickRequest(
            CharacterId: charId,
            ExecutionId: executionId1,
            TimeBucket: timeBucket,
            CurrentTime: DateTime.UtcNow
        );

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

            // Attempt 1 fails because character doesn't exist yet
            var res1 = await orchestrator.ExecuteTickAsync(request1);
            Assert.False(res1.Success);
            Assert.Equal(AutonomyTickStatus.Failed, res1.Tick!.Status);
        }

        // Now the character is added to DB (e.g. transient issue resolved)
        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.SaveChangesAsync();
        }

        // Attempt 2: Controlled retry of the same Failed (CharacterId, TimeBucket)
        var executionId2 = Guid.NewGuid();
        var request2 = new AutonomyTickRequest(
            CharacterId: charId,
            ExecutionId: executionId2,
            TimeBucket: timeBucket,
            CurrentTime: DateTime.UtcNow
        );

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

            var res2 = await orchestrator.ExecuteTickAsync(request2);
            Assert.True(res2.Success);
            Assert.False(res2.IsDuplicateSuppressed);
            Assert.Equal(executionId2, res2.ExecutionId);
            Assert.Equal(AutonomyTickStatus.Completed, res2.Tick!.Status);
            Assert.NotNull(res2.Tick.CompletedAt);
            Assert.Null(res2.Tick.FailedAt);
        }

        // Attempt 3: Retry after Completed -> Must be suppressed
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

            var res3 = await orchestrator.ExecuteTickAsync(new AutonomyTickRequest(charId, Guid.NewGuid(), timeBucket, DateTime.UtcNow));
            Assert.True(res3.Success);
            Assert.True(res3.IsDuplicateSuppressed);
            Assert.Null(res3.Tick);
        }
    }

    [Fact]
    public void CharacterAutonomyTick_DomainTerminalState_PreventsInvalidTransitions()
    {
        var tick = CharacterAutonomyTick.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-08-28T12:00");
        Assert.Equal(AutonomyTickStatus.Running, tick.Status);

        // Complete the tick
        tick.Complete(DateTime.UtcNow);
        Assert.Equal(AutonomyTickStatus.Completed, tick.Status);

        // Cannot Complete again or Fail from Completed
        Assert.Throws<InvalidOperationException>(() => tick.Complete(DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => tick.Fail(DateTime.UtcNow, "Error"));
        Assert.Throws<InvalidOperationException>(() => tick.ReclaimForRetry(Guid.NewGuid(), DateTime.UtcNow));
    }
}
