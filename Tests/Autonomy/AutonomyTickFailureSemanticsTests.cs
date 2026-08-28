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
    private readonly DbContextOptions<CoreDbContext> _options;

    public AutonomyTickFailureSemanticsTests()
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
    public async Task FailureDuringExecution_TransitionsTickToFailedStatus_AndPreservesExecutionIdentity()
    {
        var nonExistentCharacterId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var timeBucket = "2026-08-28T18:00";

        using var db = new CoreDbContext(_options);
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

            // Attempt 1 fails because character doesn't exist yet
            var res1 = await orchestrator.ExecuteTickAsync(request1);
            Assert.False(res1.Success);
            Assert.Equal(AutonomyTickStatus.Failed, res1.Tick!.Status);
        }

        // Now the character is added to DB (e.g. transient issue resolved)
        using (var db = new CoreDbContext(_options))
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

            var res2 = await orchestrator.ExecuteTickAsync(request2);
            Assert.True(res2.Success);
            Assert.False(res2.IsDuplicateSuppressed);
            Assert.Equal(executionId2, res2.ExecutionId);
            Assert.Equal(AutonomyTickStatus.Completed, res2.Tick!.Status);
            Assert.NotNull(res2.Tick.CompletedAt);
            Assert.Null(res2.Tick.FailedAt);
        }

        // Attempt 3: Retry after Completed -> Must be suppressed
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

            var res3 = await orchestrator.ExecuteTickAsync(new AutonomyTickRequest(charId, Guid.NewGuid(), timeBucket, DateTime.UtcNow));
            Assert.True(res3.Success);
            Assert.True(res3.IsDuplicateSuppressed);
            Assert.Null(res3.Tick);
        }
    }

    [Fact]
    public async Task TenConcurrentRetryWorkers_AttemptingSameFailedTick_AllowsExactlyOneWinnerAndNineSuppressed_WithExactDBState()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar and Arcane Researcher", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var goal = new CharacterGoal(charId, "Master Alchemical Research", CharacterGoalType.SkillDevelopment, 100);
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Great alchemical research discovery!");
        var timeBucket = "2026-08-28T23:30";

        // Step 1: Pre-populate a Failed tick in the database
        var initialFailedTick = CharacterAutonomyTick.Create(
            characterId: charId,
            executionId: Guid.NewGuid(),
            timeBucket: timeBucket,
            startedAt: DateTime.UtcNow.AddMinutes(-5),
            worldEventId: worldEvent.Id
        );
        initialFailedTick.Fail(DateTime.UtcNow.AddMinutes(-4), "Simulated transient DB connection fault");

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.CharacterWorldEvents.AddAsync(worldEvent);
            await db.CharacterAutonomyTicks.AddAsync(initialFailedTick);
            await db.SaveChangesAsync();
        }

        // Step 2: 10 concurrent workers simultaneously attempt to retry the Failed tick with distinct ExecutionIds
        var workerExecutionIds = Enumerable.Range(1, 10).Select(_ => Guid.NewGuid()).ToList();

        var tasks = workerExecutionIds.Select(async execId =>
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
                ExecutionId: execId,
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

        var winningResult = results.First(r => r.Success && !r.IsDuplicateSuppressed);
        Assert.NotNull(winningResult.Tick);
        Assert.Equal(AutonomyTickStatus.Completed, winningResult.Tick.Status);

        // Step 3: Strict Invariant Assertions on Final Database State
        using (var db = new CoreDbContext(_options))
        {
            // 1. Exactly 1 row in CharacterAutonomyTicks, status Completed, winner's ExecutionId, incremented Version
            var dbTick = await db.CharacterAutonomyTicks.FirstAsync(t => t.CharacterId == charId && t.TimeBucket == timeBucket);
            Assert.Equal(AutonomyTickStatus.Completed, dbTick.Status);
            Assert.Equal(winningResult.ExecutionId, dbTick.ExecutionId);
            Assert.Equal(4, dbTick.Version); // 1 (create) + 1 (fail) + 1 (reclaim) + 1 (complete)
            Assert.NotNull(dbTick.CompletedAt);
            Assert.Null(dbTick.FailedAt);
            Assert.Null(dbTick.ErrorMessage);

            // 2. Exactly 1 Activity row
            var activityCount = await db.CharacterActivities.CountAsync(a => a.CharacterId == charId);
            Assert.Equal(1, activityCount);

            // 3. Exactly 1 Reaction row
            var reactionCount = await db.CharacterWorldEventReactions.CountAsync(r => r.CharacterId == charId);
            Assert.Equal(1, reactionCount);

            // 4. Exactly 1 SceneSpecification row
            var specCount = await db.SceneSpecifications.CountAsync(s => s.CharacterId == charId);
            Assert.Equal(1, specCount);

            // 5. Exactly 1 Goal contribution (30 from reaction + 2 from activity = 32.0, NOT multiplied by 10)
            var dbGoal = await db.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(32.0, dbGoal.CurrentValue);
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
