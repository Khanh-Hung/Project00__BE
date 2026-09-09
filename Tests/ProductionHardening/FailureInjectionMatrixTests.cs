using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Common;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.LifeSimulation;
using Application.Contracts.Safety;
using Application.Enums;
using Application.Interfaces;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.CognitiveCycle;
using Infrastructure.Services.Goals;
using Infrastructure.Services.LifeSimulation;
using Infrastructure.Services.Safety;
using Infrastructure.Services.SocialPresence;
using Infrastructure.Services.State;
using Infrastructure.Services.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.LifeSimulation;
using Xunit;

namespace Tests.ProductionHardening;

/// <summary>
/// Failure Injection Matrix Tests (Section XIX)
/// Tests critical failure boundaries to ensure:
/// 1. CognitiveCycle failure does not report false success.
/// 2. ActionExecution failure before mutation leaves state unchanged.
/// 3. Failure after mutation preserves applied side effect semantics.
/// 4. Memory feedback persistence failure does not roll back committed primary state mutation.
/// 5. Relationship feedback persistence failure does not roll back committed primary state mutation.
/// 6. Social presence feedback persistence failure does not roll back committed primary state mutation.
/// 7. Outbox persistence failure rolls back simulation activity atomically (using DbCommandInterceptor).
/// </summary>
public class FailureInjectionMatrixTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 9, 14, 0, 0, TimeSpan.Zero);

    public FailureInjectionMatrixTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
        db.EnsureLifeSimulationTriggersCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task Failure_1_CognitiveCycle_PipelineThrows_NeverReportsSuccess()
    {
        var charId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 90m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            var execService = new CharacterActionExecutionService(transitionService, new CharacterActionExecutionPolicy(), NullLogger<CharacterActionExecutionService>.Instance);

            var throwingGate = new FaultyActionSafetyGate();

            var cycleService = new CharacterCognitiveCycleService(
                stateService: stateService,
                experiencePolicy: new CharacterInternalExperiencePolicy(),
                appraisalPolicy: new CharacterAppraisalPolicy(),
                emotionPolicy: new CharacterEmotionPolicy(),
                desirePolicy: new CharacterDesirePolicy(),
                intentPolicy: new CharacterIntentPolicy(),
                actionProposalPolicy: new CharacterActionProposalPolicy(),
                safetyGate: throwingGate,
                actionExecutionService: execService,
                logger: NullLogger<CharacterCognitiveCycleService>.Instance
            );

            var context = new CharacterCognitiveCycleContext(cycleId, execId, charId, now);

            // Unhandled infrastructure exceptions MUST throw, never report false success
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cycleService.RunAsync(context));

            // State in DB MUST remain at original version and hunger
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(1, persistedState.Version);
            Assert.Equal(90m, persistedState.Hunger);
        }
    }

    [Fact]
    public async Task Failure_2_ActionExecution_FailureBeforeMutation_LeavesStateUnchanged()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 80m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var execService = new CharacterActionExecutionService(transitionService, new CharacterActionExecutionPolicy(), NullLogger<CharacterActionExecutionService>.Instance);

            // Proposal specifies expected stateVersion = 99 (while DB is version 1)
            var staleProposal = new CharacterActionProposal(
                type: ActionType.Eat,
                intensity: 0.8,
                sourceIntent: IntentType.SeekFood,
                motivation: MotivationType.HungerDriven,
                stateVersion: 99);

            var execContext = new CharacterActionExecutionContext(execId, now);
            var execResult = await execService.ExecuteAsync(charId, staleProposal, execContext);

            // Must report ConcurrencyConflict
            Assert.Equal(CharacterActionExecutionStatus.ConcurrencyConflict, execResult.Status);
            Assert.False(execResult.IsApplied);

            // State in DB MUST NOT be changed
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(1, persistedState.Version);
            Assert.Equal(80m, persistedState.Hunger);

            // Transitions table MUST be empty
            var transitions = await db.CharacterStateTransitions.ToListAsync();
            Assert.Empty(transitions);
        }
    }

    [Fact]
    public async Task Failure_3_ActionExecution_FailureAfterMutation_PreservesAppliedStateSemantics()
    {
        // Genuinely inject failure immediately after ActionExecution has committed state mutation.
        // Proves that when subsequent feedback operations fail, the cycle preserves IsApplied = true,
        // does not report false failure, and authoritative CharacterState remains committed at new version.
        var charId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 85m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            var execService = new CharacterActionExecutionService(transitionService, new CharacterActionExecutionPolicy(), NullLogger<CharacterActionExecutionService>.Instance);
            var gate = new ActionSafetyGate(stateService, new[] { new DefaultActionSafetyPolicy() }, NullLogger<ActionSafetyGate>.Instance);

            // Inject faulty memory feedback service that throws AFTER action execution has mutated state
            var faultyMemory = new FaultyMemoryFeedbackService();
            // Inject faulty relationship feedback service that throws AFTER action execution
            var faultyRel = new FaultyRelationshipFeedbackService();

            var cycleService = new CharacterCognitiveCycleService(
                stateService: stateService,
                experiencePolicy: new CharacterInternalExperiencePolicy(),
                appraisalPolicy: new CharacterAppraisalPolicy(),
                emotionPolicy: new CharacterEmotionPolicy(),
                desirePolicy: new CharacterDesirePolicy(),
                intentPolicy: new CharacterIntentPolicy(),
                actionProposalPolicy: new CharacterActionProposalPolicy(),
                safetyGate: gate,
                actionExecutionService: execService,
                logger: NullLogger<CharacterCognitiveCycleService>.Instance,
                memoryFeedbackService: faultyMemory,
                relationshipFeedbackService: faultyRel
            );

            var context = new CharacterCognitiveCycleContext(cycleId, execId, charId, now);

            // Run cycle: ActionExecution commits state mutation, then subsequent feedback services fail
            var result = await cycleService.RunAsync(context);

            // Invariant: Cycle completed with action and action execution is Applied
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
            Assert.NotNull(result.ActionExecution);
            Assert.Equal(CharacterActionExecutionStatus.Applied, result.ActionExecution.Status);
            Assert.True(result.ActionExecution.IsApplied);

            // Primary CharacterState mutation in DB MUST remain committed at version 2
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, persistedState.Version);
            Assert.True(persistedState.Hunger < 85m);

            // Transition ledger entry MUST remain committed
            var transition = await db.CharacterStateTransitions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.CharacterId == charId && t.ExecutionId == execId);
            Assert.NotNull(transition);
            Assert.Equal(1, transition.VersionBefore);
            Assert.Equal(2, transition.VersionAfter);
        }
    }

    [Fact]
    public async Task Failure_4_Feedback_MemoryStorageFailure_DoesNotRollbackCommittedPrimaryState()
    {
        var charId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 80m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            var execService = new CharacterActionExecutionService(transitionService, new CharacterActionExecutionPolicy(), NullLogger<CharacterActionExecutionService>.Instance);
            var gate = new ActionSafetyGate(stateService, new[] { new DefaultActionSafetyPolicy() }, NullLogger<ActionSafetyGate>.Instance);

            var faultyMemory = new FaultyMemoryFeedbackService();

            var cycleService = new CharacterCognitiveCycleService(
                stateService: stateService,
                experiencePolicy: new CharacterInternalExperiencePolicy(),
                appraisalPolicy: new CharacterAppraisalPolicy(),
                emotionPolicy: new CharacterEmotionPolicy(),
                desirePolicy: new CharacterDesirePolicy(),
                intentPolicy: new CharacterIntentPolicy(),
                actionProposalPolicy: new CharacterActionProposalPolicy(),
                safetyGate: gate,
                actionExecutionService: execService,
                logger: NullLogger<CharacterCognitiveCycleService>.Instance,
                memoryFeedbackService: faultyMemory
            );

            var context = new CharacterCognitiveCycleContext(cycleId, execId, charId, now);
            var result = await cycleService.RunAsync(context);

            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
            Assert.NotNull(result.ActionExecution);
            Assert.True(result.ActionExecution.IsApplied);

            // Memory feedback is null due to graceful degradation
            Assert.Null(result.MemoryFeedback);

            // Primary CharacterState mutation in DB MUST remain committed
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, persistedState.Version);
            Assert.True(persistedState.Hunger < 80m);
        }
    }

    [Fact]
    public async Task Failure_5_Feedback_RelationshipPersistenceFailure_DoesNotRollbackCommittedPrimaryState()
    {
        var charId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 80m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            var execService = new CharacterActionExecutionService(transitionService, new CharacterActionExecutionPolicy(), NullLogger<CharacterActionExecutionService>.Instance);
            var gate = new ActionSafetyGate(stateService, new[] { new DefaultActionSafetyPolicy() }, NullLogger<ActionSafetyGate>.Instance);

            var faultyRel = new FaultyRelationshipFeedbackService();

            var cycleService = new CharacterCognitiveCycleService(
                stateService: stateService,
                experiencePolicy: new CharacterInternalExperiencePolicy(),
                appraisalPolicy: new CharacterAppraisalPolicy(),
                emotionPolicy: new CharacterEmotionPolicy(),
                desirePolicy: new CharacterDesirePolicy(),
                intentPolicy: new CharacterIntentPolicy(),
                actionProposalPolicy: new CharacterActionProposalPolicy(),
                safetyGate: gate,
                actionExecutionService: execService,
                logger: NullLogger<CharacterCognitiveCycleService>.Instance,
                relationshipFeedbackService: faultyRel
            );

            var context = new CharacterCognitiveCycleContext(cycleId, execId, charId, now);
            var result = await cycleService.RunAsync(context);

            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
            Assert.NotNull(result.ActionExecution);
            Assert.True(result.ActionExecution.IsApplied);

            // Relationship feedback is null due to graceful degradation
            Assert.Null(result.RelationshipFeedback);

            // Primary CharacterState mutation in DB MUST remain committed
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, persistedState.Version);
            Assert.True(persistedState.Hunger < 80m);
        }
    }

    [Fact]
    public async Task Failure_6_Feedback_SocialPresenceFailure_DoesNotRollbackCommittedPrimaryState()
    {
        var charId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 80m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            var execService = new CharacterActionExecutionService(transitionService, new CharacterActionExecutionPolicy(), NullLogger<CharacterActionExecutionService>.Instance);
            var gate = new ActionSafetyGate(stateService, new[] { new DefaultActionSafetyPolicy() }, NullLogger<ActionSafetyGate>.Instance);

            var faultyPresence = new FaultySocialPresenceTransitionService();

            var cycleService = new CharacterCognitiveCycleService(
                stateService: stateService,
                experiencePolicy: new CharacterInternalExperiencePolicy(),
                appraisalPolicy: new CharacterAppraisalPolicy(),
                emotionPolicy: new CharacterEmotionPolicy(),
                desirePolicy: new CharacterDesirePolicy(),
                intentPolicy: new CharacterIntentPolicy(),
                actionProposalPolicy: new CharacterActionProposalPolicy(),
                safetyGate: gate,
                actionExecutionService: execService,
                logger: NullLogger<CharacterCognitiveCycleService>.Instance,
                socialPresenceTransitionService: faultyPresence
            );

            var context = new CharacterCognitiveCycleContext(cycleId, execId, charId, now);
            var result = await cycleService.RunAsync(context);

            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
            Assert.NotNull(result.ActionExecution);
            Assert.True(result.ActionExecution.IsApplied);

            // SocialPresence feedback is null due to graceful degradation
            Assert.Null(result.SocialPresenceFeedback);

            // Primary CharacterState mutation in DB MUST remain committed
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, persistedState.Version);
            Assert.True(persistedState.Hunger < 80m);
        }
    }

    [Fact]
    public async Task Failure_7_LifeSimulation_OutboxFailure_RollsBackActivityAtomically()
    {
        // Proves true atomic database transaction rollback:
        // EF Core begins transaction, executes SQL UPDATE on CharacterLifeActivities (Completed),
        // and then when executing SQL INSERT on CharacterOutboxMessages, a database failure occurs.
        // The transaction rolls back atomically, reverting the executed UPDATE in SQLite.
        var interceptor = new OutboxRollbackCommandInterceptor();
        var optionsWithInterceptor = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        var charId = Guid.NewGuid();
        var now = FixedNow;
        var activityId = Guid.NewGuid();

        // 1. Seed initial character and scheduled activity
        await using (var db = new CoreDbContext(_options))
        {
            var character = new Character(
                "Test", "Title", "https://example.com/avatar.jpg",
                "Prompt", "Hi", "Category") { Id = charId };
            db.Characters.Add(character);

            var state = new CharacterState(charId, now.UtcDateTime);
            db.CharacterStates.Add(state);

            var activity = new CharacterLifeActivity(
                characterId: charId,
                activityType: LifeActivityType.Work,
                startAtUtc: now.UtcDateTime.AddMinutes(-30),
                plannedEndAtUtc: now.UtcDateTime.AddMinutes(-5),
                status: LifeActivityStatus.Scheduled,
                metadata: null,
                createdAtUtc: now.UtcDateTime.AddMinutes(-35),
                id: activityId
            );
            db.CharacterLifeActivities.Add(activity);
            await db.SaveChangesAsync();
        }

        // 2. Run TickAsync with genuine repository and outbox repository sharing CoreDbContext
        await using (var db = new CoreDbContext(optionsWithInterceptor))
        {
            var activityRepo = new CharacterLifeActivityRepository(db);
            var outboxRepo = new CharacterOutboxRepository(db);
            var simClock = new FakeLifeSimulationClock();
            var systemClock = new SystemClock();

            var service = new LifeSimulationService(
                activityRepo,
                outboxRepo,
                simClock,
                systemClock,
                NullLogger<LifeSimulationService>.Instance);

            var simContext = new CharacterLifeSimulationContext(charId, now, Guid.NewGuid());

            // TickAsync will execute UPDATE on CharacterLifeActivities, then attempt INSERT into CharacterOutboxMessages.
            // The interceptor fails the INSERT at the SQL execution stage, triggering a database transaction rollback.
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
                service.TickAsync(simContext));

            Assert.IsType<SqliteException>(ex.InnerException);
            Assert.Contains("Simulated disk I/O error during outbox insert", ex.InnerException.Message);

            // Verify the UPDATE command actually executed against the database connection prior to outbox failure
            Assert.True(interceptor.ExecutedUpdateCount > 0,
                "The UPDATE command for CharacterLifeActivities must have actually executed against the database before outbox insertion failed.");
            Assert.True(interceptor.HasInjectedFailure,
                "The interceptor must have injected failure during the INSERT into CharacterOutboxMessages.");
        }

        // 3. Verify Atomic Rollback in Database
        await using (var db = new CoreDbContext(_options))
        {
            var reloadedActivity = await db.CharacterLifeActivities.AsNoTracking().FirstAsync(a => a.Id == activityId);
            // Activity status MUST remain Scheduled (NOT Completed!) because the transaction rolled back
            Assert.Equal(LifeActivityStatus.Scheduled, reloadedActivity.Status);
            Assert.Null(reloadedActivity.StartedAtUtc);
            Assert.Null(reloadedActivity.CompletedAtUtc);

            // Outbox messages MUST be 0 (NOT committed!)
            var outboxCount = await db.CharacterOutboxMessages.AsNoTracking().CountAsync();
            Assert.Equal(0, outboxCount);
        }
    }

    public sealed class OutboxRollbackCommandInterceptor : DbCommandInterceptor
    {
        public int ExecutedUpdateCount { get; private set; }
        public bool HasInjectedFailure { get; private set; }

        public override DbDataReader ReaderExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result)
        {
            TrackExecuted(command);
            return base.ReaderExecuted(command, eventData, result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            TrackExecuted(command);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CheckAndInject(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CheckAndInject(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            CheckAndInject(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CheckAndInject(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void TrackExecuted(DbCommand command)
        {
            var sql = command.CommandText ?? string.Empty;
            if (sql.Contains("CharacterLifeActivities", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                ExecutedUpdateCount++;
            }
        }

        private void CheckAndInject(DbCommand command)
        {
            var sql = command.CommandText ?? string.Empty;
            if (sql.Contains("CharacterOutboxMessages", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                HasInjectedFailure = true;
                throw new SqliteException("Simulated disk I/O error during outbox insert within atomic transaction.", 10);
            }
        }
    }

    private sealed class FaultyActionSafetyGate : IActionSafetyGate
    {
        public Task<SafetyDecision> EvaluateAsync(Guid characterId, CharacterActionProposal proposal, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulated safety evaluation infrastructure failure.");
        }
    }

    private sealed class FaultyMemoryFeedbackService : ICharacterMemoryFeedbackService
    {
        public Task<CharacterMemoryFeedback?> RecordFeedbackAsync(CharacterCognitiveCycleContext cycleContext, CharacterCognitiveCycleResult cycleResult, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated memory storage disk failure.");
        }
    }

    private sealed class FaultyRelationshipFeedbackService : ICharacterRelationshipFeedbackService
    {
        public Task<CharacterRelationshipFeedback?> RecordFeedbackAsync(CharacterCognitiveCycleContext cycleContext, CharacterCognitiveCycleResult cycleResult, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated relationship persistence failure.");
        }
    }

    private sealed class FaultySocialPresenceTransitionService : ISocialPresenceTransitionService
    {
        public Task<CharacterSocialPresence> GetOrCreateAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CharacterSocialPresence> ActivateAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CharacterSocialPresence> SetAwayAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CharacterSocialPresence> SetOfflineAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CharacterSocialPresence> SetActivityAsync(Guid characterId, LifeActivityType activityType, DateTimeOffset now, RelationshipTargetType? targetType = null, Guid? targetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CharacterSocialPresence> SetVisibilityAsync(Guid characterId, SocialPresenceVisibility visibility, DateTimeOffset now, CancellationToken ct = default) => throw new NotImplementedException();

        public Task<CharacterSocialPresenceFeedback?> ApplyActionExecutionFeedbackAsync(Guid characterId, Guid executionId, CharacterActionExecutionResult actionExecution, DateTimeOffset now, RelationshipTargetType? targetType = null, Guid? targetId = null, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated social presence persistence failure.");
        }
    }
}
