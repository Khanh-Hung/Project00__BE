using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.ActionExecution;
using Application.Contracts.Autonomous;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.Goals;
using Application.Interfaces;
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
using Infrastructure.Services.Safety;
using Infrastructure.Services.SocialPresence;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.SocialPresence;

public sealed class CharacterSocialPresenceCognitiveIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

    public CharacterSocialPresenceCognitiveIntegrationTests()
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

    private async Task<Guid> SeedCharacterStateAsync(
        decimal hunger = 50m,
        decimal energy = 80m,
        decimal stress = 50m,
        decimal socialNeed = 90m,
        decimal comfort = 50m)
    {
        var charId = Guid.NewGuid();
        await using var db = new CoreDbContext(_options);
        var state = new CharacterState(
            charId,
            initializedAtUtc: FixedNow.UtcDateTime,
            hunger: hunger,
            energy: energy,
            stress: stress,
            socialNeed: socialNeed,
            comfort: comfort
        );
        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();
        return charId;
    }

    private (CharacterCognitiveCycleService cycleService, AutonomousCharacterService autoService) CreateServices(
        CoreDbContext db,
        IActionSafetyGate? safetyGate = null,
        ICharacterSocialPresenceRepository? overridePresenceRepo = null)
    {
        var transitionService = new CharacterStateTransitionService(
            db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateEvolutionPolicy = new CharacterStateEvolutionPolicy();
        var stateService = new CharacterStateService(
            db, transitionService, stateEvolutionPolicy, NullLogger<CharacterStateService>.Instance);

        var execPolicy = new CharacterActionExecutionPolicy();
        var execService = new CharacterActionExecutionService(
            transitionService, execPolicy, NullLogger<CharacterActionExecutionService>.Instance);

        var gate = safetyGate ?? new ActionSafetyGate(
            stateService,
            new[] { new DefaultActionSafetyPolicy() },
            NullLogger<ActionSafetyGate>.Instance);

        var goalRepo = new CharacterGoalRepository(db);
        var goalPolicy = new CharacterGoalPolicy();
        var goalService = new CharacterGoalService(db, goalRepo, goalPolicy, NullLogger<CharacterGoalService>.Instance);

        var presenceRepo = overridePresenceRepo ?? new CharacterSocialPresenceRepository(db);
        var presenceTransition = new SocialPresenceTransitionService(presenceRepo, NullLogger<SocialPresenceTransitionService>.Instance);
        var socialPolicy = new SocialBehaviorPolicy();

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
            goalService: goalService,
            socialBehaviorPolicy: socialPolicy,
            socialPresenceTransitionService: presenceTransition,
            socialPresenceRepository: presenceRepo
        );

        var tickRepo = new CharacterAutonomousLifeTickRepository(db);
        var autoService = new AutonomousCharacterService(
            cycleService,
            tickRepo,
            NullLogger<AutonomousCharacterService>.Instance
        );

        return (cycleService, autoService);
    }

    [Fact]
    public async Task FullAutonomousCycle_WithSocialGoal_GeneratesSocialActionAndUpdatesPresence()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 95m, energy: 80m);
        var tickId = Guid.NewGuid();

        // 1. Seed active social goal and active social presence
        using (var db = new CoreDbContext(_options))
        {
            var goalRepo = new CharacterGoalRepository(db);
            var goal = new CharacterGoal(
                charId,
                "Socialize",
                FixedNow,
                CharacterGoalType.Relationship,
                targetValue: 100,
                priority: CharacterGoalPriority.High,
                initialStatus: CharacterGoalStatus.Active,
                initialProgress: 10
            );
            await goalRepo.AddAsync(goal);

            var presenceRepo = new CharacterSocialPresenceRepository(db);
            await presenceRepo.TryCreateAsync(CharacterSocialPresence.CreateDefault(charId, FixedNow));
        }

        // 2. Run autonomous cycle
        using (var db = new CoreDbContext(_options))
        {
            var (_, autoService) = CreateServices(db);
            var result = await autoService.RunOnceAsync(charId, tickId, FixedNow);

            Assert.True(result.IsSuccess);
            Assert.Equal(AutonomousCycleStatus.Executed, result.Status);
            Assert.Equal(ActionType.Socialize, result.ActionProposal?.Proposal?.Type);
            Assert.NotNull(result.SocialPresenceFeedback);
            Assert.Equal(LifeActivityType.Socialize, result.SocialPresenceFeedback.CurrentActivityType);
        }

        // 3. Verify DB state: Social presence updated to Socialize activity
        using (var db = new CoreDbContext(_options))
        {
            var presenceRepo = new CharacterSocialPresenceRepository(db);
            var presence = await presenceRepo.GetByCharacterIdAsync(charId);

            Assert.NotNull(presence);
            Assert.Equal(LifeActivityType.Socialize, presence.CurrentActivityType);
            Assert.Equal(2u, presence.Version);

            var transitions = await presenceRepo.GetRecentTransitionsAsync(charId, 10);
            Assert.Single(transitions);
            Assert.Equal("Socialize", transitions[0].ActionType);
        }
    }

    [Fact]
    public async Task SafetyGateDenial_BlocksAction_NoStateMutation_NoSocialPresenceTransition()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 95m, energy: 80m);
        var tickId = Guid.NewGuid();

        using (var db = new CoreDbContext(_options))
        {
            var goalRepo = new CharacterGoalRepository(db);
            var goal = new CharacterGoal(
                charId, "Socialize", FixedNow, CharacterGoalType.Relationship,
                targetValue: 100, priority: CharacterGoalPriority.High,
                initialStatus: CharacterGoalStatus.Active, initialProgress: 10);
            await goalRepo.AddAsync(goal);

            var presenceRepo = new CharacterSocialPresenceRepository(db);
            await presenceRepo.TryCreateAsync(CharacterSocialPresence.CreateDefault(charId, FixedNow));
        }

        // Mock a safety gate that denies all actions
        var denyingGate = new DenyingSafetyGate();

        using (var db = new CoreDbContext(_options))
        {
            var (_, autoService) = CreateServices(db, safetyGate: denyingGate);
            var result = await autoService.RunOnceAsync(charId, tickId, FixedNow);

            Assert.Equal(AutonomousCycleStatus.SafetyBlocked, result.Status);
            Assert.Null(result.SocialPresenceFeedback);
        }

        // Verify social presence is unchanged (remains Idle, 0 transitions in DB)
        using (var db = new CoreDbContext(_options))
        {
            var presenceRepo = new CharacterSocialPresenceRepository(db);
            var presence = await presenceRepo.GetByCharacterIdAsync(charId);

            Assert.NotNull(presence);
            Assert.Equal(LifeActivityType.Idle, presence.CurrentActivityType);
            Assert.Equal(1u, presence.Version);

            var transitions = await presenceRepo.GetRecentTransitionsAsync(charId, 10);
            Assert.Empty(transitions);
        }
    }

    [Fact]
    public async Task Idempotency_DuplicateExecutionReplay_ReturnsAlreadyExecutedWithoutDuplicateTransition()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 95m, energy: 80m);
        var tickId = Guid.NewGuid();

        using (var db = new CoreDbContext(_options))
        {
            var goalRepo = new CharacterGoalRepository(db);
            var goal = new CharacterGoal(
                charId, "Socialize", FixedNow, CharacterGoalType.Relationship,
                targetValue: 100, priority: CharacterGoalPriority.High,
                initialStatus: CharacterGoalStatus.Active, initialProgress: 10);
            await goalRepo.AddAsync(goal);

            var presenceRepo = new CharacterSocialPresenceRepository(db);
            await presenceRepo.TryCreateAsync(CharacterSocialPresence.CreateDefault(charId, FixedNow));
        }

        // Run 1: Executed
        using (var db = new CoreDbContext(_options))
        {
            var (_, autoService) = CreateServices(db);
            var result1 = await autoService.RunOnceAsync(charId, tickId, FixedNow);
            Assert.True(result1.IsSuccess);
        }

        // Run 2: Re-run same tick
        using (var db = new CoreDbContext(_options))
        {
            var (_, autoService) = CreateServices(db);
            var result2 = await autoService.RunOnceAsync(charId, tickId, FixedNow);

            Assert.True(result2.IsSuccess);
            Assert.Equal(AutonomousCycleStatus.Executed, result2.Status);

            var presenceRepo = new CharacterSocialPresenceRepository(db);
            var transitions = await presenceRepo.GetRecentTransitionsAsync(charId, 10);
            Assert.Single(transitions); // Exactly 1 transition persisted
        }
    }

    [Fact]
    public async Task FailureIsolation_SocialPresenceError_DoesNotRollbackStateOrFailCycle()
    {
        var charId = await SeedCharacterStateAsync(socialNeed: 95m, energy: 80m);
        var tickId = Guid.NewGuid();

        using (var db = new CoreDbContext(_options))
        {
            var goalRepo = new CharacterGoalRepository(db);
            var goal = new CharacterGoal(
                charId, "Socialize", FixedNow, CharacterGoalType.Relationship,
                targetValue: 100, priority: CharacterGoalPriority.High,
                initialStatus: CharacterGoalStatus.Active, initialProgress: 10);
            await goalRepo.AddAsync(goal);

            var presenceRepo = new CharacterSocialPresenceRepository(db);
            await presenceRepo.TryCreateAsync(CharacterSocialPresence.CreateDefault(charId, FixedNow));
        }

        // Faulty presence repository that throws on update
        var faultyRepo = new FaultySocialPresenceRepository();

        using (var db = new CoreDbContext(_options))
        {
            var (cycleService, _) = CreateServices(db, overridePresenceRepo: faultyRepo);

            var cycleContext = new CharacterCognitiveCycleContext(
                CycleId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                CharacterId: charId,
                TriggeredAtUtc: FixedNow,
                Event: new AutonomousCognitiveEvent(Guid.NewGuid(), charId, tickId, FixedNow)
            );

            var result = await cycleService.RunAsync(cycleContext);

            // Invariant: Action execution applied and state committed despite social presence error
            Assert.True(result.IsSuccess);
            Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
            Assert.NotNull(result.ActionExecution);
            Assert.True(result.ActionExecution.IsApplied);
        }

        // Verify state was actually mutated in DB
        using (var db = new CoreDbContext(_options))
        {
            var state = await db.CharacterStates.FirstOrDefaultAsync(s => s.CharacterId == charId);
            Assert.NotNull(state);
            Assert.True(state.Version > 1); // State mutated successfully
        }
    }

    [Fact]
    public async Task AntiInjection_CallerProvidedSocialPresenceContext_IsRejected()
    {
        var charId = await SeedCharacterStateAsync();
        using var db = new CoreDbContext(_options);
        var (cycleService, _) = CreateServices(db);

        var injectedContext = new CharacterSocialPresenceContext(
            PresenceId: Guid.NewGuid(),
            CharacterId: charId,
            Status: SocialPresenceStatus.Active,
            CurrentActivityType: LifeActivityType.Idle,
            Visibility: SocialPresenceVisibility.Public,
            StartedAtUtc: FixedNow,
            UpdatedAtUtc: FixedNow
        );

        var cycleContext = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: FixedNow,
            SocialPresenceContext: injectedContext
        );

        var result = await cycleService.RunAsync(cycleContext);

        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, result.Status);
        Assert.Contains("SocialPresenceContext cannot be pre-populated", result.Message);
    }

    private sealed class DenyingSafetyGate : IActionSafetyGate
    {
        public Task<SafetyDecision> EvaluateAsync(Guid characterId, CharacterActionProposal proposal, CancellationToken ct = default)
        {
            return Task.FromResult(SafetyDecision.Denied("TEST_SAFETY_BLOCK", "Test safety policy blocked action."));
        }
    }

    private sealed class FaultySocialPresenceRepository : ICharacterSocialPresenceRepository
    {
        public Task<CharacterSocialPresence?> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default) =>
            Task.FromResult<CharacterSocialPresence?>(CharacterSocialPresence.CreateDefault(characterId, FixedNow));

        public Task<(bool IsCreated, CharacterSocialPresence Presence)> TryCreateAsync(CharacterSocialPresence candidate, CancellationToken ct = default) =>
            Task.FromResult((true, candidate));

        public Task<CharacterSocialPresence> UpdateAsync(CharacterSocialPresence presence, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated database failure during social presence update.");

        public Task<CharacterSocialPresenceTransition> AddTransitionAsync(CharacterSocialPresenceTransition transition, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated database failure during transition insert.");

        public Task<CharacterSocialPresenceTransition?> GetTransitionAsync(Guid characterId, Guid executionId, CancellationToken ct = default) =>
            Task.FromResult<CharacterSocialPresenceTransition?>(null);

        public Task<IReadOnlyList<CharacterSocialPresenceTransition>> GetRecentTransitionsAsync(Guid characterId, int limit = 10, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CharacterSocialPresenceTransition>>(Array.Empty<CharacterSocialPresenceTransition>());

        public Task<(CharacterSocialPresence Presence, CharacterSocialPresenceTransition Transition, bool IsDuplicate)> RecordTransitionAtomicAsync(
            Guid characterId,
            Guid executionId,
            string actionType,
            LifeActivityType targetActivity,
            RelationshipTargetType? targetType,
            Guid? targetId,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated database failure during atomic transition record.");
    }
}
