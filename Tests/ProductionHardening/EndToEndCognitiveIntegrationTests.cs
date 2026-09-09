using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Common;
using Application.Contracts.ActionExecution;
using Application.Contracts.Autonomous;
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
/// End-to-End Cognitive Integration Tests (Sections XIV - XVIII)
/// Verifies the full production lifecycle of Character AI across:
/// 1. User Message pipeline
/// 2. World Event pipeline (Outbox -> Consumer -> Cycle)
/// 3. Autonomous Tick pipeline (SimulationTickId -> AutoTick -> Cycle -> Goal -> Action -> State)
/// 4. Social Presence feedback & atomic ledger
/// 5. Safety Denial E2E
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

        var memoryRepo = new CharacterMemoryRepository(db);
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
    public async Task E2E_1_UserMessage_FullCycle_StimulusToStateMutationAndFeedback()
    {
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            // Seed authoritative initial state: High hunger triggers Eat
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 85m, energy: 70m, stress: 20m);
            db.CharacterStates.Add(state);

            // Seed relationship
            var rel = CharacterRelationship.Create(charId, userId, 20, CharacterMood.Neutral, 30, now.UtcDateTime);
            db.CharacterRelationships.Add(rel);

            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var (cycleService, _, _) = CreateFullPipeline(db);

            var userMsgEvent = new UserMessageCognitiveEvent(
                EventId: eventId,
                CharacterId: charId,
                OccurredAtUtc: now,
                Source: userId.ToString(),
                Message: "Here is some food for you!");

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

            // Verify State Mutation in DB (Single Source of Truth)
            var persistedState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, persistedState.Version); // Version incremented 1 -> 2
            Assert.True(persistedState.Hunger < 85m); // Hunger decreased through ActionExecution

            // Verify State Transition recorded in ledger
            var transition = await db.CharacterStateTransitions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.CharacterId == charId && t.ExecutionId == executionId);
            Assert.NotNull(transition);
            Assert.Equal(1, transition.VersionBefore);
            Assert.Equal(2, transition.VersionAfter);

            // Verify Memory Feedback was persisted
            var memory = await db.CharacterMemories.AsNoTracking()
                .FirstOrDefaultAsync(m => m.CharacterId == charId && m.ExecutionId == executionId);
            Assert.NotNull(memory);
        }
    }

    [Fact]
    public async Task E2E_2_WorldEvent_OutboxToConsumerToCognitiveCycle()
    {
        var charId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 20m, energy: 30m, stress: 70m);
            db.CharacterStates.Add(state);

            // Simulate LifeSimulation creating transactional outbox message
            var outboxMessage = new CharacterOutboxMessage(
                id: Guid.NewGuid(),
                eventId: eventId,
                characterId: charId,
                eventType: "LifeSimulationEvent",
                payloadJson: "{\"EventName\":\"LoudThunderstorm\",\"Category\":\"Weather\"}",
                fingerprint: "weather_storm_fingerprint",
                occurredAtUtc: now.UtcDateTime,
                createdAtUtc: now.UtcDateTime);

            db.CharacterOutboxMessages.Add(outboxMessage);
            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var (_, _, worldConsumer) = CreateFullPipeline(db);

            var worldEvent = new WorldCognitiveEvent(
                EventId: eventId,
                CharacterId: charId,
                OccurredAtUtc: now,
                Source: "LifeSimulation",
                EventName: "LoudThunderstorm",
                Category: "Weather");

            // Consume World Event via Authoritative Consumer
            var consumptionResult = await worldConsumer.ConsumeAsync(worldEvent);

            Assert.True(consumptionResult.IsAccepted);
            Assert.Equal(eventId, consumptionResult.EventId);
            Assert.Equal(charId, consumptionResult.CharacterId);
            Assert.NotNull(consumptionResult.CycleId);

            // Verify consumption record in DB
            var consumptionRecord = await db.WorldCognitiveEventConsumptions.AsNoTracking()
                .FirstOrDefaultAsync(c => c.EventId == eventId);
            Assert.NotNull(consumptionRecord);
            Assert.Equal(EventConsumptionState.Consumed, consumptionRecord.State);

            // Replay same event -> IDEMPOTENT, must not produce second cycle
            var replayResult = await worldConsumer.ConsumeAsync(worldEvent);
            Assert.True(replayResult.IsDuplicate);
            Assert.Equal(consumptionResult.CycleId, replayResult.CycleId);

            // Verify count of cycles in consumption ledger remains 1
            var count = await db.WorldCognitiveEventConsumptions.CountAsync(c => c.EventId == eventId);
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task E2E_3_AutonomousTick_PreservesTickIdentityAndAtMostOnceExecution()
    {
        var charId = Guid.NewGuid();
        var simulationTickId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            // Seed state: High social need + high energy -> Socialize desire
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 10m, energy: 90m, stress: 10m, socialNeed: 95m);
            db.CharacterStates.Add(state);

            // Seed active Goal for autonomous cycle
            var goal = new CharacterGoal(
                charId,
                "Socialize",
                CharacterGoalType.Relationship,
                100.0,
                now,
                CharacterGoalPriority.High);
            db.CharacterGoals.Add(goal);

            await db.SaveChangesAsync();
        }

        await using (var db = new CoreDbContext(_options))
        {
            var (_, autoService, _) = CreateFullPipeline(db);

            // Run Autonomous Life Tick
            var tickResult = await autoService.RunOnceAsync(charId, simulationTickId, now);

            Assert.True(tickResult.IsSuccess);
            Assert.Equal(AutonomousCycleStatus.Executed, tickResult.Status);
            Assert.Equal(charId, tickResult.CharacterId);
            Assert.Equal(simulationTickId, tickResult.SimulationTickId);
            Assert.Equal(now, tickResult.SimulationTimeUtc);

            // Assert ActionExecution happened
            Assert.NotNull(tickResult.ActionExecutionResult);
            Assert.True(tickResult.ActionExecutionResult.IsApplied);

            // Verify durable tick record in DB
            var tickRecord = await db.CharacterAutonomousLifeTicks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.CharacterId == charId && t.SimulationTickId == simulationTickId);
            Assert.NotNull(tickRecord);
            Assert.Equal(AutonomousTickState.Completed, tickRecord.State);

            // Replay same tick -> IDEMPOTENT, must NOT execute action again
            var replayResult = await autoService.RunOnceAsync(charId, simulationTickId, now);
            Assert.Equal(AutonomousCycleStatus.Executed, replayResult.Status);
            Assert.Contains("already completed", replayResult.Message);

            // State version must remain 2 (only ONE execution happened)
            var state = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(2, state.Version);
        }
    }

    [Fact]
    public async Task E2E_4_SocialPresence_AtomicTransitionAndDivergentConflict()
    {
        var charA = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(db);
            var service = new SocialPresenceTransitionService(repo, NullLogger<SocialPresenceTransitionService>.Instance);

            var actionExecution = CharacterActionExecutionResult.Applied(
                executionId: execId,
                characterId: charA,
                proposal: new CharacterActionProposal(ActionType.Socialize, 0.8, IntentType.SeekSocialConnection, MotivationType.ConnectionDriven, 1),
                versionBefore: 1,
                versionAfter: 2,
                delta: new CharacterStateDelta(socialNeedDelta: -20m),
                snapshot: new CharacterState(charA, now.UtcDateTime).ToSnapshot());

            // 1. First execution creates presence and transition record atomically
            var feedback = await service.ApplyActionExecutionFeedbackAsync(
                characterId: charA,
                executionId: execId,
                actionExecution: actionExecution,
                now: now);

            Assert.NotNull(feedback);
            Assert.True(feedback.IsSuccess);
            Assert.Equal(LifeActivityType.Socialize, feedback.CurrentActivityType);

            // 2. Replay with identical payload is idempotent
            var replay = await service.ApplyActionExecutionFeedbackAsync(
                characterId: charA,
                executionId: execId,
                actionExecution: actionExecution,
                now: now);

            Assert.NotNull(replay);
            Assert.Equal(feedback.PresenceId, replay.PresenceId);

            // 3. Divergent payload for same ExecutionId produces Conflict
            var divergentAction = CharacterActionExecutionResult.Applied(
                executionId: execId, // SAME ExecutionId
                characterId: charA,
                proposal: new CharacterActionProposal(ActionType.Rest, 0.8, IntentType.SeekRest, MotivationType.RestorationDriven, 1),
                versionBefore: 1,
                versionAfter: 2,
                delta: new CharacterStateDelta(energyDelta: 20m),
                snapshot: new CharacterState(charA, now.UtcDateTime).ToSnapshot());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApplyActionExecutionFeedbackAsync(
                    characterId: charA,
                    executionId: execId,
                    actionExecution: divergentAction,
                    now: now));
        }
    }

    [Fact]
    public async Task E2E_5_SafetyDenial_ProposalBlockedBySafetyGate_ZeroStateMutations()
    {
        var charId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var now = FixedNow;

        await using (var db = new CoreDbContext(_options))
        {
            var state = new CharacterState(charId, now.UtcDateTime, hunger: 90m, energy: 80m);
            db.CharacterStates.Add(state);
            await db.SaveChangesAsync();
        }

        // Create safety policy that strictly denies all Eat actions
        var denyingPolicy = new DenyEatSafetyPolicy();

        await using (var db = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            var execPolicy = new CharacterActionExecutionPolicy();
            var execService = new CharacterActionExecutionService(transitionService, execPolicy, NullLogger<CharacterActionExecutionService>.Instance);

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

            // Assert ZERO CharacterState mutations in DB
            var finalState = await db.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
            Assert.Equal(1, finalState.Version); // Version unchanged
            Assert.Equal(90m, finalState.Hunger); // Hunger unchanged

            // Assert ZERO transitions in ledger
            var transitions = await db.CharacterStateTransitions.ToListAsync();
            Assert.Empty(transitions);
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
