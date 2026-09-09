using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Application.Contracts.Autonomous;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.Goals;
using Application.Contracts.LifeSimulation;
using Application.Contracts.Safety;
using Application.Enums;
using Application.Interfaces;
using Application.Services.LifeSimulation;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.Autonomous;
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
/// End-to-End Cognitive Integration Tests (Section XVIII)
/// Complete end-to-end integration tests proving full lifecycle flows:
/// 1. UserMessage stimulus -> cognitive cycle -> action -> state mutation + multi-domain feedback (Memory, Relationship, Personality, Goal, SocialPresence).
/// 2. LifeSimulation -> Outbox -> Adapter -> WorldCognitiveEventConsumer -> CognitiveCycle -> State & Consumption ledger.
/// 3. Autonomous life tick sequential replay preserves tick identity and at-most-once execution.
/// 4. Autonomous life tick concurrent race enforces at-most-once execution under concurrent workers.
/// 5. Social presence transition through full cognitive loop.
/// 6. Direct integration: SocialPresence atomic transition & divergent conflict detection.
/// 7. Safety denial blocks action with verified ZERO mutations across all subsystems.
/// </summary>
public class EndToEndCognitiveIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    public EndToEndCognitiveIntegrationTests()
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

    private (CharacterCognitiveCycleService cycleService,
             AutonomousCharacterService autoService,
             WorldCognitiveEventConsumer worldConsumer) CreateFullPipeline(
        CoreDbContext db,
        IActionSafetyGate? safetyGate = null)
    {
        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
        var execPolicy = new CharacterActionExecutionPolicy();
        var execService = new CharacterActionExecutionService(transitionService, execPolicy, NullLogger<CharacterActionExecutionService>.Instance);

        var gate = safetyGate ?? new ActionSafetyGate(
            stateService,
            new[] { new DefaultActionSafetyPolicy() },
            NullLogger<ActionSafetyGate>.Instance);

        var goalRepo = new CharacterGoalRepository(db);
        var goalService = new CharacterGoalService(db, goalRepo, new CharacterGoalPolicy(), NullLogger<CharacterGoalService>.Instance);

        var presenceRepo = new CharacterSocialPresenceRepository(db);
        var presenceTransition = new SocialPresenceTransitionService(presenceRepo, NullLogger<SocialPresenceTransitionService>.Instance);
        var socialPolicy = new SocialBehaviorPolicy();

        var memoryRetrieval = new CharacterMemoryRetrievalService(db, NullLogger<CharacterMemoryRetrievalService>.Instance);
        var memoryFeedback = new CharacterMemoryFeedbackService(db, NullLogger<CharacterMemoryFeedbackService>.Instance);

        var relRepo = new CharacterRelationshipRepository(db);
        var relRetrieval = new CharacterRelationshipRetrievalService(relRepo, NullLogger<CharacterRelationshipRetrievalService>.Instance);
        var relTransition = new CharacterRelationshipTransitionService(db, NullLogger<CharacterRelationshipTransitionService>.Instance);
        var relFeedback = new CharacterRelationshipFeedbackService(relTransition, new DefaultCharacterRelationshipFeedbackPolicy(), NullLogger<CharacterRelationshipFeedbackService>.Instance);

        var personalityRepo = new CharacterPersonalityRepository(db);
        var personalityAdaptation = new PersonalityAdaptationService(personalityRepo, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance);

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
            memoryRetrievalService: memoryRetrieval,
            memoryFeedbackService: memoryFeedback,
            relationshipRetrievalService: relRetrieval,
            relationshipFeedbackService: relFeedback,
            personalityAdaptationService: personalityAdaptation,
            personalityRepository: personalityRepo,
            goalService: goalService,
            socialBehaviorPolicy: socialPolicy,
            socialPresenceTransitionService: presenceTransition,
            socialPresenceRepository: presenceRepo
        );

        var tickRepo = new CharacterAutonomousLifeTickRepository(db);
        var autoService = new AutonomousCharacterService(cycleService, tickRepo, NullLogger<AutonomousCharacterService>.Instance);

        var consumptionRepo = new WorldCognitiveEventConsumptionRepository(db);
        var worldConsumer = new WorldCognitiveEventConsumer(consumptionRepo, cycleService, new SystemClock(), NullLogger<WorldCognitiveEventConsumer>.Instance);

        return (cycleService, autoService, worldConsumer);
    }

    [Fact]
    public async Task E2E_1_UserMessage_FullCycle_StimulusToStateMutationAndMultiDomainFeedback()
    {
        // Full cognitive pipeline verified across ALL feedback domains:
        // Stimulus -> State -> Perception -> Experience -> Appraisal -> Emotion -> Desire -> Goal -> Intent -> ActionProposal -> SafetyGate -> ActionExecution -> CharacterStateTransition -> Memory + Relationship + Personality + Goal + SocialPresence feedback.
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var now = FixedNow;

        // 1. Seed Character and all domain entities
        await using (var db = new CoreDbContext(_options))
        {
            var character = new Character(
                "E2E Test Character", "Hero", "https://example.com/avatar.jpg",
                "Prompt", "Hi", "Category") { Id = charId };
            db.Characters.Add(character);

            // Authoritative initial state: High hunger triggers Eat
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 85m, energy: 70m, stress: 20m);
            db.CharacterStates.Add(state);

            // Seed relationship with user
            var rel = CharacterRelationship.Create(
                characterId: charId,
                targetType: RelationshipTargetType.User,
                targetId: userId,
                relationshipType: RelationshipType.Acquaintance,
                trust: 20,
                affection: 30,
                familiarity: 10,
                initialTimestamp: now.UtcDateTime);
            db.CharacterRelationships.Add(rel);

            // Seed personality
            var personality = CharacterPersonality.CreateDefault(charId);
            db.CharacterPersonalities.Add(personality);

            // Seed active Goal for eating
            var goal = new CharacterGoal(
                charId,
                "Eat",
                now,
                CharacterGoalType.Lifestyle,
                targetValue: 100,
                priority: CharacterGoalPriority.High,
                initialProgress: 0
            );
            db.CharacterGoals.Add(goal);

            // Seed social presence
            var presence = CharacterSocialPresence.CreateDefault(charId, now);
            db.CharacterSocialPresences.Add(presence);

            await db.SaveChangesAsync();
        }

        // 2. Execute Full Cognitive Cycle
        await using (var db = new CoreDbContext(_options))
        {
            var (cycleService, _, _) = CreateFullPipeline(db);

            var userMsgEvent = new UserMessageCognitiveEvent(
                EventId: eventId,
                CharacterId: charId,
                OccurredAtUtc: now,
                Message: "Here is some food for you!",
                Source: "User",
                UserId: userId);

            var context = new CharacterCognitiveCycleContext(
                CycleId: cycleId,
                ExecutionId: executionId,
                CharacterId: charId,
                TriggeredAtUtc: now,
                Event: userMsgEvent);

            // RUN FULL COGNITIVE PIPELINE
            var result = await cycleService.RunAsync(context);

            // Assert pipeline outputs
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
            Assert.Equal(charId, result.CharacterId);
            Assert.Equal(cycleId, result.CycleId);
            Assert.Equal(executionId, result.ExecutionId);
            Assert.Equal(eventId, result.Event!.EventId);

            // Assert Safety Gate was invoked and allowed
            Assert.NotNull(result.SafetyDecision);
            Assert.True(result.SafetyDecision.IsAllowed);

            // Assert Proposal was formed and executed
            Assert.NotNull(result.ActionProposal?.Proposal);
            Assert.Equal(ActionType.Eat, result.ActionProposal.Proposal.Type);

            // Assert ActionExecution status is Applied
            Assert.NotNull(result.ActionExecution);
            Assert.Equal(CharacterActionExecutionStatus.Applied, result.ActionExecution.Status);

            // 1. Verify Primary State Mutation in DB (Single Source of Truth)
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, persistedState.Version); // Version incremented 1 -> 2
            Assert.True(persistedState.Hunger < 85m); // Hunger decreased through ActionExecution

            // 2. Verify State Transition recorded in ledger
            var transition = await db.CharacterStateTransitions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.CharacterId == charId && t.ExecutionId == executionId);
            Assert.NotNull(transition);
            Assert.Equal(1, transition.VersionBefore);
            Assert.Equal(2, transition.VersionAfter);

            // 3. Verify Memory Feedback
            Assert.NotNull(result.MemoryFeedback);
            var memory = await db.CharacterMemories.AsNoTracking()
                .FirstOrDefaultAsync(m => m.CharacterId == charId && m.ExecutionId == executionId);
            Assert.NotNull(memory);
            Assert.Equal(CharacterMemoryFeedbackType.ActionCompleted, memory.FeedbackType);

            // 4. Verify Relationship Feedback
            Assert.NotNull(result.RelationshipFeedback);
            Assert.Equal(1, result.RelationshipFeedback.TrustDelta);
            Assert.Equal(1, result.RelationshipFeedback.AffectionDelta);
            var updatedRel = await db.CharacterRelationships.AsNoTracking()
                .FirstAsync(r => r.CharacterId == charId && r.TargetId == userId);
            Assert.Equal(21, updatedRel.Trust); // 20 + 1
            Assert.Equal(31, updatedRel.Affection); // 30 + 1

            // 5. Verify Personality Adaptation
            Assert.NotNull(result.PersonalityAdaptations);
            Assert.Contains(result.PersonalityAdaptations, a => a.TraitKey == PersonalityTraitKeys.Warmth);

            // 6. Verify Goal Progress Feedback
            Assert.NotNull(result.GoalFeedback);
            Assert.Equal(0, result.GoalFeedback.PreviousProgress);
            Assert.Equal(25, result.GoalFeedback.NewProgress);
            Assert.False(result.GoalFeedback.IsDuplicateExecution);
            var updatedGoal = await db.CharacterGoals.AsNoTracking()
                .FirstAsync(g => g.CharacterId == charId && g.Title == "Eat");
            Assert.Equal(25, updatedGoal.ProgressPercentage);

            // 7. Verify Social Presence Feedback
            Assert.NotNull(result.SocialPresenceFeedback);
            Assert.Equal(LifeActivityType.Eat, result.SocialPresenceFeedback.CurrentActivityType);
            var updatedPresence = await db.CharacterSocialPresences.AsNoTracking()
                .FirstAsync(p => p.CharacterId == charId);
            Assert.Equal(LifeActivityType.Eat, updatedPresence.CurrentActivityType);
            var presenceTransition = await db.CharacterSocialPresenceTransitions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.CharacterId == charId && t.ExecutionId == executionId);
            Assert.NotNull(presenceTransition);
            Assert.Equal(LifeActivityType.Eat, presenceTransition.NewActivityType);
        }
    }

    [Fact]
    public async Task E2E_2_WorldEvent_SimulationOutboxToConsumerToCognitiveCycle()
    {
        // Genuine end-to-end integration:
        // LifeSimulation completes activity -> writes CharacterOutboxMessage to DB ->
        // Read outbox from DB -> deserialize CharacterOutboxPayload ->
        // Adapt to WorldCognitiveEvent -> WorldCognitiveEventConsumer ->
        // CognitiveCycle execution -> consumption ledger committed in DB.
        var charId = Guid.NewGuid();
        var now = FixedNow;
        var activityId = Guid.NewGuid();

        // 1. Seed Character and Scheduled Activity
        await using (var db = new CoreDbContext(_options))
        {
            var character = new Character(
                "Simulated Character", "Civilian", "https://example.com/avatar.jpg",
                "Prompt", "Hi", "Category") { Id = charId };
            db.Characters.Add(character);

            var state = new CharacterState(charId, now.UtcDateTime, hunger: 50m, energy: 50m);
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

        // 2. LifeSimulationService runs tick: completes activity and commits CharacterOutboxMessage
        await using (var db = new CoreDbContext(_options))
        {
            var activityRepo = new CharacterLifeActivityRepository(db);
            var outboxRepo = new CharacterOutboxRepository(db);
            var simClock = new FakeLifeSimulationClock();
            var systemClock = new SystemClock();

            var lifeSimService = new LifeSimulationService(
                activityRepo,
                outboxRepo,
                simClock,
                systemClock,
                NullLogger<LifeSimulationService>.Instance);

            var simContext = new CharacterLifeSimulationContext(charId, now, Guid.NewGuid());
            var tickResult = await lifeSimService.TickAsync(simContext);

            Assert.True(tickResult.IsSuccess);
            Assert.NotEmpty(tickResult.Events);
        }

        // 3. Read Outbox message from DB, adapt, and feed to WorldCognitiveEventConsumer
        await using (var db = new CoreDbContext(_options))
        {
            var outboxMsg = await db.CharacterOutboxMessages.AsNoTracking()
                .FirstOrDefaultAsync(m => m.CharacterId == charId);
            Assert.NotNull(outboxMsg);

            // Deserialize payload from genuine outbox JSON
            var payload = CharacterOutboxPayload.FromJson(outboxMsg.PayloadJson);
            Assert.NotNull(payload);

            var simEvent = new LifeSimulationEvent(
                payload.EventId,
                payload.CharacterId,
                payload.OccurredAtUtc,
                payload.ActivityId,
                payload.ActivityType,
                payload.EventType,
                payload.Description);

            // Adapt using production adapter
            var worldCognitiveEvent = LifeSimulationCognitiveEventAdapter.ToCognitiveEvent(simEvent);
            Assert.Equal(outboxMsg.EventId, worldCognitiveEvent.EventId);
            Assert.Equal(charId, worldCognitiveEvent.CharacterId);

            // Ingest through WorldCognitiveEventConsumer
            var (cycleService, _, worldConsumer) = CreateFullPipeline(db);

            var consumptionResult = await worldConsumer.ConsumeAsync(worldCognitiveEvent);

            Assert.True(consumptionResult.IsAccepted);
            Assert.Equal(WorldCognitiveEventConsumptionStatus.Processed, consumptionResult.Status);
            Assert.Equal(worldCognitiveEvent.EventId, consumptionResult.EventId);
            Assert.Equal(charId, consumptionResult.CharacterId);

            // Verify consumption state in DB ledger
            var consumption = await db.WorldCognitiveEventConsumptions.AsNoTracking()
                .FirstOrDefaultAsync(c => c.EventId == worldCognitiveEvent.EventId);
            Assert.NotNull(consumption);
            Assert.Equal(EventConsumptionState.Consumed, consumption.State);

            // Verify Idempotency: replay returns Duplicate
            var replayResult = await worldConsumer.ConsumeAsync(worldCognitiveEvent);
            Assert.True(replayResult.IsDuplicate);
            Assert.Equal(WorldCognitiveEventConsumptionStatus.Duplicate, replayResult.Status);
        }
    }

    [Fact]
    public async Task E2E_3_AutonomousTick_PreservesTickIdentityAndAtMostOnceExecution()
    {
        var charId = Guid.NewGuid();
        var tickId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 85m);
            db.CharacterStates.Add(state);

            var goal = new CharacterGoal(
                charId,
                "Eat",
                now,
                CharacterGoalType.Lifestyle,
                targetValue: 100,
                priority: CharacterGoalPriority.High,
                initialProgress: 0
            );
            db.CharacterGoals.Add(goal);

            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var (_, autoService, _) = CreateFullPipeline(db);

            // First execution
            var firstRun = await autoService.RunOnceAsync(charId, tickId, now);

            Assert.True(firstRun.IsSuccess);
            Assert.Equal(AutonomousCycleStatus.Executed, firstRun.Status);
            Assert.NotNull(firstRun.ActionExecutionResult);
            Assert.True(firstRun.ActionExecutionResult.IsApplied);
            Assert.Equal(tickId, firstRun.SimulationTickId);
            Assert.NotEqual(tickId, firstRun.CycleId); // Identity separation

            // Replay with identical tick identity
            var replayRun = await autoService.RunOnceAsync(charId, tickId, now);

            // Idempotent rejection: at-most-once semantics
            Assert.Equal(tickId, replayRun.SimulationTickId);
            Assert.Equal(firstRun.CycleId, replayRun.CycleId); // Preserves original CycleId reference
            Assert.Null(replayRun.ActionExecutionResult); // Suppressed
            Assert.Contains("already", replayRun.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // Verify DB has exactly ONE tick record
            var ticks = await db.CharacterAutonomousLifeTicks.AsNoTracking()
                .Where(t => t.CharacterId == charId && t.SimulationTickId == tickId)
                .ToListAsync();
            Assert.Single(ticks);
            Assert.Equal(AutonomousTickState.Completed, ticks[0].State);
        }
    }

    [Fact]
    public async Task E2E_4_AutonomousTick_ConcurrentWorkers_EnforcesAtMostOnceExecution()
    {
        // Deterministic concurrent race test: Two workers attempt the same (CharacterId, SimulationTickId)
        // simultaneously. Exactly ONE must succeed (ActionExecutionResult applied); the other must be rejected as duplicate.
        var charId = Guid.NewGuid();
        var tickId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 85m);
            db.CharacterStates.Add(state);

            var goal = new CharacterGoal(
                charId,
                "Eat",
                now,
                CharacterGoalType.Lifestyle,
                targetValue: 100,
                priority: CharacterGoalPriority.High,
                initialProgress: 0
            );
            db.CharacterGoals.Add(goal);

            await db.SaveChangesAsync();
        }

        // Run concurrent workers using separate DbContext instances over the shared connection
        await using var db1 = new CoreDbContext(_options);
        await using var db2 = new CoreDbContext(_options);

        var (_, autoService1, _) = CreateFullPipeline(db1);
        var (_, autoService2, _) = CreateFullPipeline(db2);

        var task1 = Task.Run(() => autoService1.RunOnceAsync(charId, tickId, now));
        var task2 = Task.Run(() => autoService2.RunOnceAsync(charId, tickId, now));

        var results = await Task.WhenAll(task1, task2);

        // Exactly ONE winner and exactly ONE duplicate
        var executedCount = results.Count(r => r.ActionExecutionResult != null && r.ActionExecutionResult.IsApplied);
        var duplicateCount = results.Count(r => r.ActionExecutionResult == null && (r.Message?.Contains("already", StringComparison.OrdinalIgnoreCase) == true));

        Assert.Equal(1, executedCount);
        Assert.Equal(1, duplicateCount);

        // Database invariants: Exactly 1 tick record, exactly 1 state transition, state version bumped exactly once
        await using (var db = new CoreDbContext(_options))
        {
            var ticks = await db.CharacterAutonomousLifeTicks.AsNoTracking()
                .Where(t => t.CharacterId == charId && t.SimulationTickId == tickId)
                .ToListAsync();
            Assert.Single(ticks);
            Assert.Equal(AutonomousTickState.Completed, ticks[0].State);

            var transitions = await db.CharacterStateTransitions.AsNoTracking()
                .Where(t => t.CharacterId == charId)
                .ToListAsync();
            Assert.Single(transitions);

            var finalState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, finalState.Version);
        }
    }

    [Fact]
    public async Task E2E_5_SocialPresence_CycleStimulusToActionToPresenceTransition()
    {
        // Proves social presence transition driven end-to-end through the cognitive loop:
        // Stimulus -> CognitiveCycle -> ActionProposal -> SafetyGate -> ActionExecution -> SocialPresenceFeedback -> DB State.
        var charId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 85m);
            db.CharacterStates.Add(state);

            var presence = CharacterSocialPresence.CreateDefault(charId, now);
            db.CharacterSocialPresences.Add(presence);

            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var (cycleService, _, _) = CreateFullPipeline(db);

            var context = new CharacterCognitiveCycleContext(cycleId, execId, charId, now);
            var result = await cycleService.RunAsync(context);

            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
            Assert.NotNull(result.SocialPresenceFeedback);
            Assert.Equal(LifeActivityType.Eat, result.SocialPresenceFeedback.CurrentActivityType);

            // Verify in DB that presence was atomically updated
            var persistedPresence = await db.CharacterSocialPresences.AsNoTracking()
                .FirstAsync(p => p.CharacterId == charId);
            Assert.Equal(LifeActivityType.Eat, persistedPresence.CurrentActivityType);
            Assert.Equal(2u, persistedPresence.Version);

            // Verify presence transition record exists in DB
            var transition = await db.CharacterSocialPresenceTransitions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.CharacterId == charId && t.ExecutionId == execId);
            Assert.NotNull(transition);
            Assert.Equal(LifeActivityType.Eat, transition.NewActivityType);
        }
    }

    [Fact]
    public async Task Integration_SocialPresence_AtomicTransitionAndDivergentConflict()
    {
        var charA = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var presenceRepo = new CharacterSocialPresenceRepository(db);
            var service = new SocialPresenceTransitionService(presenceRepo, NullLogger<SocialPresenceTransitionService>.Instance);

            var proposal1 = new CharacterActionProposal(
                type: ActionType.Socialize,
                intensity: 0.8,
                sourceIntent: IntentType.SeekSocialConnection,
                motivation: MotivationType.ConnectionDriven,
                stateVersion: 1);

            var actionExecution = CharacterActionExecutionResult.Applied(
                executionId: execId,
                characterId: charA,
                proposal: proposal1,
                versionBefore: 1,
                versionAfter: 2,
                delta: new CharacterStateDelta(),
                snapshot: new CharacterStateSnapshot(energy: 50, hunger: 50, version: 2));

            // First transition: Socialize
            var feedback1 = await service.ApplyActionExecutionFeedbackAsync(
                characterId: charA,
                executionId: execId,
                actionExecution: actionExecution,
                now: now);

            Assert.NotNull(feedback1);
            Assert.Equal(LifeActivityType.Socialize, feedback1.CurrentActivityType);

            // Idempotent replay: identical action type
            var feedbackReplay = await service.ApplyActionExecutionFeedbackAsync(
                characterId: charA,
                executionId: execId,
                actionExecution: actionExecution,
                now: now);

            Assert.NotNull(feedbackReplay);
            Assert.Equal(LifeActivityType.Socialize, feedbackReplay.CurrentActivityType);

            // Divergent semantic replay with divergent ActionType: MUST throw InvalidOperationException
            var proposalDivergent = new CharacterActionProposal(
                type: ActionType.Rest,
                intensity: 0.8,
                sourceIntent: IntentType.SeekRest,
                motivation: MotivationType.RestorationDriven,
                stateVersion: 1);

            var divergentAction = CharacterActionExecutionResult.Applied(
                executionId: execId,
                characterId: charA,
                proposal: proposalDivergent,
                versionBefore: 1,
                versionAfter: 2,
                delta: new CharacterStateDelta(),
                snapshot: new CharacterStateSnapshot(energy: 50, hunger: 50, version: 2));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApplyActionExecutionFeedbackAsync(
                    characterId: charA,
                    executionId: execId,
                    actionExecution: divergentAction,
                    now: now));
        }
    }

    [Fact]
    public async Task E2E_6_SafetyDenial_ProposalBlockedBySafetyGate_ZeroSubsystemMutations()
    {
        // Proves that when SafetyGate denies an action proposal, ZERO mutations occur
        // across ALL subsystems: State, StateTransitions, Memories, Relationships, Personality, Goals, and SocialPresence.
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        // 1. Seed all subsystems
        await using (var db = new CoreDbContext(_options))
        {
            var character = new Character(
                "Safety Character", "Guardian", "https://example.com/avatar.jpg",
                "Prompt", "Hi", "Category") { Id = charId };
            db.Characters.Add(character);

            var state = new CharacterState(charId, now.UtcDateTime, hunger: 90m, energy: 80m);
            db.CharacterStates.Add(state);

            var rel = CharacterRelationship.Create(
                characterId: charId,
                targetType: RelationshipTargetType.User,
                targetId: userId,
                relationshipType: RelationshipType.Acquaintance,
                trust: 20,
                affection: 30,
                familiarity: 10,
                initialTimestamp: now.UtcDateTime);
            db.CharacterRelationships.Add(rel);

            var personality = CharacterPersonality.CreateDefault(charId);
            db.CharacterPersonalities.Add(personality);

            var goal = new CharacterGoal(
                charId,
                "Eat",
                now,
                CharacterGoalType.Lifestyle,
                targetValue: 100,
                priority: CharacterGoalPriority.High,
                initialProgress: 0
            );
            db.CharacterGoals.Add(goal);

            var presence = CharacterSocialPresence.CreateDefault(charId, now);
            db.CharacterSocialPresences.Add(presence);

            await db.SaveChangesAsync();
        }

        // Create safety policy that strictly denies all Eat actions
        var denyingPolicy = new DenyEatSafetyPolicy();

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

            var strictGate = new ActionSafetyGate(
                stateService,
                new[] { denyingPolicy },
                NullLogger<ActionSafetyGate>.Instance);

            var (cycleService, _, _) = CreateFullPipeline(db, safetyGate: strictGate);

            var context = new CharacterCognitiveCycleContext(
                CycleId: cycleId,
                ExecutionId: execId,
                CharacterId: charId,
                TriggeredAtUtc: now);

            // RUN CYCLE
            var result = await cycleService.RunAsync(context);

            // Assert Safety Gate denied the proposal
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithoutAction, result.Status);
            Assert.NotNull(result.SafetyDecision);
            Assert.False(result.SafetyDecision.IsAllowed);
            Assert.Equal("DENY_EAT_FOR_TEST", result.SafetyDecision.PolicyCode);

            // Assert NO ActionExecution occurred
            Assert.Null(result.ActionExecution);

            // 1. Assert ZERO CharacterState mutations in DB
            var finalState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(1, finalState.Version); // Version unchanged
            Assert.Equal(90m, finalState.Hunger); // Hunger unchanged

            // 2. Assert ZERO state transitions in ledger
            var transitions = await db.CharacterStateTransitions.ToListAsync();
            Assert.Empty(transitions);

            // 3. Assert ZERO successful action execution memories
            var actionMemories = await db.CharacterMemories.AsNoTracking()
                .Where(m => m.CharacterId == charId && m.FeedbackType == CharacterMemoryFeedbackType.ActionCompleted)
                .ToListAsync();
            Assert.Empty(actionMemories);

            // 4. Assert Relationship values unchanged
            var persistedRel = await db.CharacterRelationships.AsNoTracking()
                .FirstAsync(r => r.CharacterId == charId && r.TargetId == userId);
            Assert.Equal(20, persistedRel.Trust);
            Assert.Equal(30, persistedRel.Affection);

            // 5. Assert Personality unchanged (no adaptations)
            var persistedPersonality = await db.CharacterPersonalities.AsNoTracking()
                .FirstAsync(p => p.CharacterId == charId);
            Assert.Equal(1u, persistedPersonality.Version);

            // 6. Assert Goal unchanged (progress remains 0)
            var persistedGoal = await db.CharacterGoals.AsNoTracking()
                .FirstAsync(g => g.CharacterId == charId && g.Title == "Eat");
            Assert.Equal(0, persistedGoal.ProgressPercentage);
            var goalProgresses = await db.CharacterGoalExecutionProgresses.AsNoTracking()
                .Where(p => p.GoalId == persistedGoal.Id)
                .ToListAsync();
            Assert.Empty(goalProgresses);

            // 7. Assert SocialPresence unchanged (activity remains Idle)
            var persistedPresence = await db.CharacterSocialPresences.AsNoTracking()
                .FirstAsync(p => p.CharacterId == charId);
            Assert.Equal(LifeActivityType.Idle, persistedPresence.CurrentActivityType);
            Assert.Equal(1u, persistedPresence.Version);
            var presenceTransitions = await db.CharacterSocialPresenceTransitions.AsNoTracking()
                .Where(t => t.CharacterId == charId)
                .ToListAsync();
            Assert.Empty(presenceTransitions);
        }
    }

    private sealed class DenyEatSafetyPolicy : IActionSafetyPolicy
    {
        public int Priority => 1;

        public SafetyDecision Evaluate(CharacterActionProposal proposal, CharacterSafetyContext context)
        {
            if (proposal.Type == ActionType.Eat)
            {
                return SafetyDecision.Denied("DENY_EAT_FOR_TEST", "Eating is temporarily restricted for safety testing.");
            }
            return SafetyDecision.Allowed();
        }
    }
}
