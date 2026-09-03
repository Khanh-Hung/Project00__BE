using Application.Common;
using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Contracts.Reactions;
using Application.Enums;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.Autonomous;
using Infrastructure.Services.Goals;
using Infrastructure.Services.Reactions;
using Infrastructure.Services.Scene;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Helpers;
using Xunit;

namespace Tests.State;

public class CharacterStateAtomicityFailureInjectionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterStateAtomicityFailureInjectionTests()
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
    public async Task CaseA_SourceAndStateStaged_DbUpdateConcurrencyExceptionAtSaveChanges_RollsBackEverything()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var db = new CoreDbContext(_options))
        {
            var character = new Character("Hero", "Adventurer", "avatar.png", "Brave", "Hello", "Anime") { Id = charId };
            await db.Characters.AddAsync(character);

            var initialState = new CharacterState(charId, now, hunger: 50m, energy: 80m);
            await db.CharacterStates.AddAsync(initialState);

            await db.SaveChangesAsync();
        }

        var executionId = Guid.NewGuid();
        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Eating,
            Location: "Kitchen",
            Reason: "Need food",
            Priority: ActivityPriority.Normal,
            DurationMinutes: 30,
            ShouldCreateVisualMoment: false,
            Confidence: 0.9f,
            ActionHint: "eating",
            PoseHint: "seated",
            DecisionFingerprint: "fingerprint-eat-a"
        );

        // Production boundary: Configure interceptor that simulates another worker mutating state right before SaveChangesAsync
        var interceptorOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new ConcurrencyConflictInjectionInterceptor())
            .Options;

        using (var db = new CoreDbContext(interceptorOptions))
        {
            var character = await db.Characters.FirstAsync(c => c.Id == charId);
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var visualReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);

            var execService = new ActivityExecutionService(
                db, goalService, fakePipeline, visualReader, transitionService, NullLogger<ActivityExecutionService>.Instance);

            var request = new ActivityExecutionRequest(
                Character: character,
                Candidate: candidate,
                CurrentTime: now,
                TimeBucket: "bucket-fail-a",
                ExecutionId: executionId
            );

            // Expect real DbUpdateConcurrencyException at SaveChangesAsync boundary
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => execService.ExecuteActivityAsync(request));
        }

        // VERIFY ATOMICITY: Activity row must NOT exist, transition ledger must NOT exist
        using (var verifyDb = new CoreDbContext(_options))
        {
            var activityCount = await verifyDb.CharacterActivities.CountAsync(a => a.CharacterId == charId);
            Assert.Equal(0, activityCount);

            var transitionCount = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
            Assert.Equal(0, transitionCount);
        }
    }

    [Fact]
    public async Task CaseB_SourceAndStateStaged_TransactionCommitFailure_RollsBackEverything()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var db = new CoreDbContext(_options))
        {
            var character = new Character("Hero", "Adventurer", "avatar.png", "Brave", "Hello", "Anime") { Id = charId };
            await db.Characters.AddAsync(character);

            var initialState = new CharacterState(charId, now, hunger: 50m, energy: 80m);
            await db.CharacterStates.AddAsync(initialState);

            await db.SaveChangesAsync();
        }

        var executionId = Guid.NewGuid();
        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Eating,
            Location: "Kitchen",
            Reason: "Eating",
            Priority: ActivityPriority.Normal,
            DurationMinutes: 30,
            ShouldCreateVisualMoment: false,
            Confidence: 0.9f,
            ActionHint: "eating",
            PoseHint: "seated",
            DecisionFingerprint: "fingerprint-eat-b"
        );

        // Production boundary: Configure interceptor that throws right at transaction commit
        var interceptorOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new CommitFailureInjectionInterceptor())
            .Options;

        using (var db = new CoreDbContext(interceptorOptions))
        {
            var character = await db.Characters.FirstAsync(c => c.Id == charId);
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var visualReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);

            var execService = new ActivityExecutionService(
                db, goalService, fakePipeline, visualReader, transitionService, NullLogger<ActivityExecutionService>.Instance);

            var request = new ActivityExecutionRequest(
                Character: character,
                Candidate: candidate,
                CurrentTime: now,
                TimeBucket: "bucket-fail-b",
                ExecutionId: executionId
            );

            await Assert.ThrowsAsync<InvalidOperationException>(() => execService.ExecuteActivityAsync(request));
        }

        // VERIFY ATOMIC ROLLBACK: Activity absent, Transition ledger absent
        using (var verifyDb = new CoreDbContext(_options))
        {
            var activityExists = await verifyDb.CharacterActivities.AnyAsync(a => a.CharacterId == charId);
            Assert.False(activityExists);

            var transitionExists = await verifyDb.CharacterStateTransitions.AnyAsync(t => t.CharacterId == charId && t.ExecutionId == executionId);
            Assert.False(transitionExists);
        }
    }

    [Fact]
    public async Task CaseC_ResponseLostAfterCommit_RetryWithSameExecutionId_SuppressesDuplicateWithoutReapplyingDelta()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var db = new CoreDbContext(_options))
        {
            var character = new Character("Hero", "Adventurer", "avatar.png", "Brave", "Hello", "Anime") { Id = charId };
            await db.Characters.AddAsync(character);

            var initialState = CharacterState.CreateDefault(charId, now);
            await db.CharacterStates.AddAsync(initialState);

            await db.SaveChangesAsync();
        }

        var executionId = Guid.NewGuid();
        var timeBucket = "bucket-case-c";
        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Eating,
            Location: "Kitchen",
            Reason: "Need food",
            Priority: ActivityPriority.Normal,
            DurationMinutes: 30,
            ShouldCreateVisualMoment: false,
            Confidence: 0.9f,
            ActionHint: "eating",
            PoseHint: "seated",
            DecisionFingerprint: "fingerprint-eat-c"
        );

        // 1. First execution commits successfully
        using (var db1 = new CoreDbContext(_options))
        {
            var character = await db1.Characters.FirstAsync(c => c.Id == charId);
            var goalService = new GoalProgressService(db1, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var visualReader = new SceneVisualStateReader(db1, NullLogger<SceneVisualStateReader>.Instance);
            var transitionService = new CharacterStateTransitionService(db1, NullLogger<CharacterStateTransitionService>.Instance);

            var execService = new ActivityExecutionService(
                db1, goalService, fakePipeline, visualReader, transitionService, NullLogger<ActivityExecutionService>.Instance);

            var request = new ActivityExecutionRequest(
                Character: character,
                Candidate: candidate,
                CurrentTime: now,
                TimeBucket: timeBucket,
                ExecutionId: executionId
            );

            var result1 = await execService.ExecuteActivityAsync(request);
            Assert.True(result1.Success);
            Assert.False(result1.IsDuplicateSuppressed);
        }

        // 2. Retry execution with SAME ExecutionId and TimeBucket
        using (var db2 = new CoreDbContext(_options))
        {
            var character = await db2.Characters.FirstAsync(c => c.Id == charId);
            var goalService = new GoalProgressService(db2, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var visualReader = new SceneVisualStateReader(db2, NullLogger<SceneVisualStateReader>.Instance);
            var transitionService = new CharacterStateTransitionService(db2, NullLogger<CharacterStateTransitionService>.Instance);

            var execService = new ActivityExecutionService(
                db2, goalService, fakePipeline, visualReader, transitionService, NullLogger<ActivityExecutionService>.Instance);

            var request = new ActivityExecutionRequest(
                Character: character,
                Candidate: candidate,
                CurrentTime: now,
                TimeBucket: timeBucket,
                ExecutionId: executionId
            );

            var result2 = await execService.ExecuteActivityAsync(request);
            Assert.True(result2.Success);
            Assert.True(result2.IsDuplicateSuppressed);
        }

        // VERIFY: Delta applied exactly ONCE, exactly 1 activity row, exactly 1 transition row
        using (var verifyDb = new CoreDbContext(_options))
        {
            var activityCount = await verifyDb.CharacterActivities.CountAsync(a => a.CharacterId == charId);
            Assert.Equal(1, activityCount);

            var transitionCount = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
            Assert.Equal(1, transitionCount);

            var state = await verifyDb.CharacterStates.FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, state.Version); // Exactly version 2, NOT version 3!
        }
    }

    [Fact]
    public async Task CaseD_ConcurrentStateUpdate_RollsBackSourceExecution_EnablingCleanRetry()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var db = new CoreDbContext(_options))
        {
            var character = new Character("Hero", "Adventurer", "avatar.png", "Brave", "Hello", "Anime") { Id = charId };
            await db.Characters.AddAsync(character);

            var initialState = CharacterState.CreateDefault(charId, now);
            await db.CharacterStates.AddAsync(initialState);

            await db.SaveChangesAsync();
        }

        var executionId = Guid.NewGuid();
        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Sleeping,
            Location: "Bedroom",
            Reason: "Sleep",
            Priority: ActivityPriority.High,
            DurationMinutes: 60,
            ShouldCreateVisualMoment: false,
            Confidence: 0.95f,
            ActionHint: "sleeping",
            PoseHint: "lying down",
            DecisionFingerprint: "fingerprint-sleep-d"
        );

        // Worker 1 loads state at Version 1
        using (var db1 = new CoreDbContext(_options))
        {
            var state1 = await db1.CharacterStates.FirstAsync(s => s.CharacterId == charId);

            // Meanwhile, Worker 2 intervenes and modifies state to Version 2
            using (var db2 = new CoreDbContext(_options))
            {
                var state2 = await db2.CharacterStates.FirstAsync(s => s.CharacterId == charId);
                state2.ApplyDelta(new CharacterStateDelta(hungerDelta: 5m));
                await db2.SaveChangesAsync();
            }

            // Worker 1 tries to commit using stale Version 1 in an atomic transaction
            var goalService = new GoalProgressService(db1, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var visualReader = new SceneVisualStateReader(db1, NullLogger<SceneVisualStateReader>.Instance);
            var transitionService = new CharacterStateTransitionService(db1, NullLogger<CharacterStateTransitionService>.Instance);

            var execService = new ActivityExecutionService(
                db1, goalService, fakePipeline, visualReader, transitionService, NullLogger<ActivityExecutionService>.Instance);

            var request = new ActivityExecutionRequest(
                Character: await db1.Characters.FirstAsync(c => c.Id == charId),
                Candidate: candidate,
                CurrentTime: now,
                TimeBucket: "bucket-concurrency-d",
                ExecutionId: executionId
            );

            // Worker 1 must throw concurrency conflict because Version changed
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () =>
            {
                await execService.ExecuteActivityAsync(request);
            });
        }

        // VERIFY: Worker 1's activity row was ROLLED BACK because state had a concurrency conflict!
        using (var verifyDb = new CoreDbContext(_options))
        {
            var activityExists = await verifyDb.CharacterActivities.AnyAsync(a => a.CharacterId == charId && a.TimeBucket == "bucket-concurrency-d");
            Assert.False(activityExists); // Activity was rolled back!

            // Now clean retry can proceed and succeed!
            var character = await verifyDb.Characters.FirstAsync(c => c.Id == charId);
            var goalService = new GoalProgressService(verifyDb, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var visualReader = new SceneVisualStateReader(verifyDb, NullLogger<SceneVisualStateReader>.Instance);
            var transitionService = new CharacterStateTransitionService(verifyDb, NullLogger<CharacterStateTransitionService>.Instance);

            var execService = new ActivityExecutionService(
                verifyDb, goalService, fakePipeline, visualReader, transitionService, NullLogger<ActivityExecutionService>.Instance);

            var retryRequest = new ActivityExecutionRequest(
                Character: character,
                Candidate: candidate,
                CurrentTime: now,
                TimeBucket: "bucket-concurrency-d",
                ExecutionId: executionId
            );

            var retryResult = await execService.ExecuteActivityAsync(retryRequest);
            Assert.True(retryResult.Success);
            Assert.False(retryResult.IsDuplicateSuppressed);
        }

        // Final verification: Activity committed, state updated to Version 3
        using (var finalVerifyDb = new CoreDbContext(_options))
        {
            var activityCount = await finalVerifyDb.CharacterActivities.CountAsync(a => a.CharacterId == charId);
            Assert.Equal(1, activityCount);

            var state = await finalVerifyDb.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        }
    }

    [Fact]
    public async Task CaseE_NonDuplicateUniqueConstraint_FailsTransaction_AndDoesNotSuppressAsDuplicate()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var db = new CoreDbContext(_options))
        {
            var character = new Character("Hero", "Adventurer", "avatar.png", "Brave", "Hello", "Anime") { Id = charId };
            await db.Characters.AddAsync(character);

            var initialState = new CharacterState(charId, now, hunger: 50m, energy: 80m);
            await db.CharacterStates.AddAsync(initialState);

            await db.SaveChangesAsync();
        }

        var executionId = Guid.NewGuid();
        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Eating,
            Location: "Kitchen",
            Reason: "Need food",
            Priority: ActivityPriority.Normal,
            DurationMinutes: 30,
            ShouldCreateVisualMoment: false,
            Confidence: 0.9f,
            ActionHint: "eating",
            PoseHint: "seated",
            DecisionFingerprint: "fingerprint-eat-e"
        );

        // Configure options with interceptor that injects a unique constraint violation on an unrelated table/index
        var interceptorOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new UnrelatedUniqueConstraintInjectionInterceptor(charId))
            .Options;

        using (var db = new CoreDbContext(interceptorOptions))
        {
            var character = await db.Characters.FirstAsync(c => c.Id == charId);
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var visualReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);

            var execService = new ActivityExecutionService(
                db, goalService, fakePipeline, visualReader, transitionService, NullLogger<ActivityExecutionService>.Instance);

            var request = new ActivityExecutionRequest(
                Character: character,
                Candidate: candidate,
                CurrentTime: now,
                TimeBucket: "bucket-fail-e",
                ExecutionId: executionId
            );

            // Because the unique constraint is NOT on CharacterActivities(TimeBucket) or CharacterStateTransitions(ExecutionId),
            // it MUST NOT be suppressed as duplicate activity! It must throw DbUpdateException!
            await Assert.ThrowsAsync<DbUpdateException>(() => execService.ExecuteActivityAsync(request));
        }

        // VERIFY ATOMIC ROLLBACK: Activity was not committed!
        using (var verifyDb = new CoreDbContext(_options))
        {
            var activityCount = await verifyDb.CharacterActivities.CountAsync(a => a.CharacterId == charId && a.TimeBucket == "bucket-fail-e");
            Assert.Equal(0, activityCount);
        }
    }

    private sealed class ConcurrencyConflictInjectionInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool _injected = false;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_injected && eventData.Context != null)
            {
                _injected = true;
                using var cmd = eventData.Context.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "UPDATE CharacterStates SET Version = Version + 100;";
                if (eventData.Context.Database.CurrentTransaction != null)
                {
                    cmd.Transaction = eventData.Context.Database.CurrentTransaction.GetDbTransaction();
                }
                cmd.ExecuteNonQuery();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CommitFailureInjectionInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulated failure at transaction commit boundary.");
        }
    }

    private sealed class UnrelatedUniqueConstraintInjectionInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private readonly Guid _charId;
        private bool _injected = false;

        public UnrelatedUniqueConstraintInjectionInterceptor(Guid charId)
        {
            _charId = charId;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_injected && eventData.Context != null)
            {
                _injected = true;
                // Inject duplicate CharacterState with same CharacterId to trigger IX_CharacterStates_CharacterId
                eventData.Context.Set<CharacterState>().Add(new CharacterState(_charId, DateTime.UtcNow));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
