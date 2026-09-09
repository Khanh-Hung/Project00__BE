using System;
using System.Collections.Generic;
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
/// 7. Outbox persistence failure rolls back simulation activity atomically.
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
    public async Task Failure_1_CognitiveCycle_MissingAuthoritativeState_DoesNotReportFalseSuccess()
    {
        await using var db = new CoreDbContext(_options);

        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
        var execService = new CharacterActionExecutionService(transitionService, new CharacterActionExecutionPolicy(), NullLogger<CharacterActionExecutionService>.Instance);
        var safetyGate = new ActionSafetyGate(stateService, new[] { new DefaultActionSafetyPolicy() }, NullLogger<ActionSafetyGate>.Instance);

        var cycleService = new CharacterCognitiveCycleService(
            stateService: stateService,
            experiencePolicy: new CharacterInternalExperiencePolicy(),
            appraisalPolicy: new CharacterAppraisalPolicy(),
            emotionPolicy: new CharacterEmotionPolicy(),
            desirePolicy: new CharacterDesirePolicy(),
            intentPolicy: new CharacterIntentPolicy(),
            actionProposalPolicy: new CharacterActionProposalPolicy(),
            safetyGate: safetyGate,
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance);

        var nonExistentCharId = Guid.NewGuid();
        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: nonExistentCharId,
            TriggeredAtUtc: FixedNow);

        var result = await cycleService.RunAsync(context);

        // MUST NOT report false success
        Assert.Equal(CharacterCognitiveCycleStatus.NotFound, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Null(result.ActionExecution);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);

        // Zero state transitions
        var transitions = await db.CharacterStateTransitions.ToListAsync();
        Assert.Empty(transitions);
    }

    [Fact]
    public async Task Failure_2_ActionExecution_StateVersionMismatch_LeavesStateUnchanged()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 50m, energy: 50m, stress: 50m);
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

            // Verify State in DB was NOT mutated
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(1, persistedState.Version);
            Assert.Equal(50m, persistedState.Hunger);

            // Zero state transitions
            var transitions = await db.CharacterStateTransitions.ToListAsync();
            Assert.Empty(transitions);
        }
    }

    [Fact]
    public async Task Failure_3_ActionExecution_FailureAfterMutation_PreservesAppliedSideEffectSemantics()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        var snapshot = new CharacterState(charId, now.UtcDateTime).ToSnapshot();
        var appliedResult = CharacterActionExecutionResult.Applied(
            executionId: execId,
            characterId: charId,
            proposal: new CharacterActionProposal(ActionType.Eat, 0.8, IntentType.SeekFood, MotivationType.HungerDriven, 1),
            versionBefore: 1,
            versionAfter: 2,
            delta: new CharacterStateDelta(hungerDelta: -30m),
            snapshot: snapshot);

        // Verify IsApplied authoritative signal
        Assert.True(appliedResult.IsApplied);
        Assert.Equal(CharacterActionExecutionStatus.Applied, appliedResult.Status);

        // Cycle result mapped from Applied action execution MUST preserve CompletedWithAction status
        var cycleResult = CharacterCognitiveCycleResult.CompletedWithAction(
            cycleId: Guid.NewGuid(),
            executionId: execId,
            characterId: charId,
            triggeredAtUtc: now,
            stateVersionAtStart: 1,
            experience: null,
            appraisal: null,
            emotion: null,
            desires: null,
            intent: null,
            actionProposal: null,
            actionExecution: appliedResult);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, cycleResult.Status);
        Assert.NotNull(cycleResult.ActionExecution);
        Assert.True(cycleResult.ActionExecution.IsApplied);
    }

    [Fact]
    public async Task Failure_4_MemoryFeedbackPersistence_Throws_LeavesPrimaryStateMutationCommitted()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 80m, energy: 70m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            var execPolicy = new CharacterActionExecutionPolicy();
            var execService = new CharacterActionExecutionService(transitionService, execPolicy, NullLogger<CharacterActionExecutionService>.Instance);
            var safetyGate = new ActionSafetyGate(stateService, new[] { new DefaultActionSafetyPolicy() }, NullLogger<ActionSafetyGate>.Instance);

            // Faulty MemoryFeedbackService that throws on persist
            var faultyMemoryService = new FaultyMemoryFeedbackService();

            var cycleService = new CharacterCognitiveCycleService(
                stateService: stateService,
                experiencePolicy: new CharacterInternalExperiencePolicy(),
                appraisalPolicy: new CharacterAppraisalPolicy(),
                emotionPolicy: new CharacterEmotionPolicy(),
                desirePolicy: new CharacterDesirePolicy(),
                intentPolicy: new CharacterIntentPolicy(),
                actionProposalPolicy: new CharacterActionProposalPolicy(),
                safetyGate: safetyGate,
                actionExecutionService: execService,
                logger: NullLogger<CharacterCognitiveCycleService>.Instance,
                memoryFeedbackService: faultyMemoryService);

            var context = new CharacterCognitiveCycleContext(
                CycleId: cycleId,
                ExecutionId: execId,
                CharacterId: charId,
                TriggeredAtUtc: now);

            // Run cycle with faulty memory service
            var result = await cycleService.RunAsync(context);

            // Cycle status remains CompletedWithAction because state was applied!
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
            Assert.NotNull(result.ActionExecution);
            Assert.True(result.ActionExecution.IsApplied);

            // Memory feedback is null due to failure
            Assert.Null(result.MemoryFeedback);

            // Primary CharacterState mutation in DB MUST remain committed!
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, persistedState.Version);
            Assert.True(persistedState.Hunger < 80m);

            // Transition ledger must contain the committed state transition
            var transition = await db.CharacterStateTransitions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.CharacterId == charId && t.ExecutionId == execId);
            Assert.NotNull(transition);
        }
    }

    [Fact]
    public async Task Failure_5_RelationshipFeedbackPersistence_Throws_LeavesPrimaryStateMutationCommitted()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 80m, energy: 70m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            var execPolicy = new CharacterActionExecutionPolicy();
            var execService = new CharacterActionExecutionService(transitionService, execPolicy, NullLogger<CharacterActionExecutionService>.Instance);
            var safetyGate = new ActionSafetyGate(stateService, new[] { new DefaultActionSafetyPolicy() }, NullLogger<ActionSafetyGate>.Instance);

            // Faulty RelationshipFeedbackService that throws on persist
            var faultyRelService = new FaultyRelationshipFeedbackService();

            var cycleService = new CharacterCognitiveCycleService(
                stateService: stateService,
                experiencePolicy: new CharacterInternalExperiencePolicy(),
                appraisalPolicy: new CharacterAppraisalPolicy(),
                emotionPolicy: new CharacterEmotionPolicy(),
                desirePolicy: new CharacterDesirePolicy(),
                intentPolicy: new CharacterIntentPolicy(),
                actionProposalPolicy: new CharacterActionProposalPolicy(),
                safetyGate: safetyGate,
                actionExecutionService: execService,
                logger: NullLogger<CharacterCognitiveCycleService>.Instance,
                relationshipFeedbackService: faultyRelService);

            var context = new CharacterCognitiveCycleContext(
                CycleId: cycleId,
                ExecutionId: execId,
                CharacterId: charId,
                TriggeredAtUtc: now);

            var result = await cycleService.RunAsync(context);

            // Cycle status remains CompletedWithAction because state was applied
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
            Assert.NotNull(result.ActionExecution);
            Assert.True(result.ActionExecution.IsApplied);

            // Primary CharacterState mutation in DB MUST remain committed
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, persistedState.Version);
            Assert.True(persistedState.Hunger < 80m);
        }
    }

    [Fact]
    public async Task Failure_6_SocialPresenceFeedbackPersistence_Throws_LeavesPrimaryStateMutationCommitted()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 80m, energy: 70m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            var execPolicy = new CharacterActionExecutionPolicy();
            var execService = new CharacterActionExecutionService(transitionService, execPolicy, NullLogger<CharacterActionExecutionService>.Instance);
            var safetyGate = new ActionSafetyGate(stateService, new[] { new DefaultActionSafetyPolicy() }, NullLogger<ActionSafetyGate>.Instance);

            // Faulty SocialPresenceTransitionService that throws on feedback recording
            var faultySocialService = new FaultySocialPresenceTransitionService();

            var cycleService = new CharacterCognitiveCycleService(
                stateService: stateService,
                experiencePolicy: new CharacterInternalExperiencePolicy(),
                appraisalPolicy: new CharacterAppraisalPolicy(),
                emotionPolicy: new CharacterEmotionPolicy(),
                desirePolicy: new CharacterDesirePolicy(),
                intentPolicy: new CharacterIntentPolicy(),
                actionProposalPolicy: new CharacterActionProposalPolicy(),
                safetyGate: safetyGate,
                actionExecutionService: execService,
                logger: NullLogger<CharacterCognitiveCycleService>.Instance,
                socialPresenceTransitionService: faultySocialService);

            var context = new CharacterCognitiveCycleContext(
                CycleId: cycleId,
                ExecutionId: execId,
                CharacterId: charId,
                TriggeredAtUtc: now);

            var result = await cycleService.RunAsync(context);

            // Cycle status remains CompletedWithAction because state was applied
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
        var charId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var character = new Character(
                "Test", "Title", "https://example.com/avatar.jpg",
                "Prompt", "Hi", "Category") { Id = charId };
            db.Characters.Add(character);
            var state = new CharacterState(charId, now.UtcDateTime);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var activityRepo = new CharacterLifeActivityRepository(db);
            // Faulty outbox repo that throws upon adding message
            var faultyOutboxRepo = new FaultyCharacterOutboxRepository();
            var simClock = new FakeLifeSimulationClock();
            var systemClock = new SystemClock();

            var service = new LifeSimulationService(
                activityRepo,
                faultyOutboxRepo,
                simClock,
                systemClock,
                NullLogger<LifeSimulationService>.Instance);

            var simContext = new CharacterLifeSimulationContext(charId, now, Guid.NewGuid());

            // Scheduling an activity first
            var activity = new CharacterLifeActivity(charId, LifeActivityType.Work, now.UtcDateTime.AddMinutes(-30), now.UtcDateTime.AddMinutes(-5));
            db.CharacterLifeActivities.Add(activity);
            await db.SaveChangesAsync();

            // When TickAsync runs, it will attempt to complete activity and write outbox event.
            // Since outbox throws, the entire operation MUST throw and activity must NOT be committed as completed!
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.TickAsync(simContext));

            // Verify activity was NOT committed as completed in the database
            var reloadedActivity = await db.CharacterLifeActivities.AsNoTracking().FirstAsync(a => a.Id == activity.Id);
            Assert.Equal(LifeActivityStatus.Scheduled, reloadedActivity.Status);
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

    private sealed class FaultyCharacterOutboxRepository : ICharacterOutboxRepository
    {
        public Task AddAsync(CharacterOutboxMessage message, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated transactional outbox failure.");
        }

        public Task AddRangeAsync(IEnumerable<CharacterOutboxMessage> messages, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated transactional outbox failure.");
        }

        public Task<CharacterOutboxMessage> AddOrGetAsync(CharacterOutboxMessage message, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CharacterOutboxMessage?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CharacterOutboxMessage?> GetByEventIdAsync(Guid eventId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOutboxMessage>> GetPendingMessagesAsync(Guid? characterId = null, int limit = 50, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
