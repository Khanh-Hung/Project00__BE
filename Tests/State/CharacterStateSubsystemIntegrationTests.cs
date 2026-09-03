using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Contracts.Autonomy;
using Application.Contracts.Reactions;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.Autonomous;
using Infrastructure.Services.Autonomy;
using Infrastructure.Services.Goals;
using Infrastructure.Services.Reactions;
using Infrastructure.Services.Scene;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Helpers;
using Xunit;

namespace Tests.State;

public sealed class CharacterStateSubsystemIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterStateSubsystemIntegrationTests()
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
    public async Task Reaction_ProducesStateDelta_PersistedInStateAndLedger()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var character = new Character("Kaelen", "Warrior", "avatar.png", "Brave", "Hello", "Anime") { Id = charId };
        var worldEvent = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "UserDirectMessage",
            sourceId: "msg-123",
            occurredAt: now,
            payloadJson: "{\"content\":\"Master praised Kaelen warmly.\"}"
        );

        using var db = new CoreDbContext(_options);
        await db.Characters.AddAsync(character);
        await db.CharacterWorldEvents.AddAsync(worldEvent);
        await db.SaveChangesAsync();

        var stateTransitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, stateTransitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
        await stateService.GetOrCreateInitialStateAsync(charId, now);

        var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
        var fakePipeline = new FakeSceneCompositionPipelineService();
        var visualReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
        var activityExecutionService = new ActivityExecutionService(db, goalService, fakePipeline, visualReader, stateTransitionService, NullLogger<ActivityExecutionService>.Instance);
        var reactionService = new CharacterReactionExecutionService(
            db, goalService, activityExecutionService, fakePipeline, visualReader, stateTransitionService, NullLogger<CharacterReactionExecutionService>.Instance);

        var execId = Guid.NewGuid();
        var req = new ReactionExecutionRequest(
            WorldEvent: worldEvent,
            Character: character,
            ExecutionId: execId,
            CurrentTime: now
        );

        var result = await reactionService.ExecuteReactionAsync(req);

        Assert.True(result.Success);

        // Verify CharacterState in DB was mutated by the reaction
        var state = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.True(state.Version > 1);

        // Verify transition ledger contains Reaction entry
        var transition = await db.CharacterStateTransitions.FirstOrDefaultAsync(t => t.CharacterId == charId && t.ExecutionId == execId);
        Assert.NotNull(transition);
        Assert.Equal("Reaction", transition.SourceType);
    }

    [Fact]
    public async Task ActivityExecution_AppliesOutcomeDelta_PersistedInStateAndLedger()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var character = new Character("Aria", "Chef", "avatar.png", "Kind", "Hello", "Anime") { Id = charId };

        using var db = new CoreDbContext(_options);
        await db.Characters.AddAsync(character);
        await db.SaveChangesAsync();

        var stateTransitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, stateTransitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
        await stateService.GetOrCreateInitialStateAsync(charId, now);

        var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
        var fakePipeline = new FakeSceneCompositionPipelineService();
        var visualReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
        var activityExecutionService = new ActivityExecutionService(
            db, goalService, fakePipeline, visualReader, stateTransitionService, NullLogger<ActivityExecutionService>.Instance);

        var execId = Guid.NewGuid();
        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Eating,
            Location: "Dining Hall",
            Reason: "Time to eat.",
            Priority: ActivityPriority.Normal,
            DurationMinutes: 30,
            ShouldCreateVisualMoment: false,
            Confidence: 1.0f,
            ActionHint: "eating",
            PoseHint: "seated",
            DecisionFingerprint: "fingerprint-eat"
        );

        var req = new ActivityExecutionRequest(
            Character: character,
            Candidate: candidate,
            CurrentTime: now,
            TimeBucket: "2026-09-03T10:00",
            ExecutionId: execId
        );

        var result = await activityExecutionService.ExecuteActivityAsync(req);

        Assert.True(result.Success);

        // Verify state: Eating reduces hunger (-40)
        var state = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.True(state.Hunger < 20m);
        Assert.Equal(2, state.Version);

        // Verify transition ledger
        var transition = await db.CharacterStateTransitions.FirstOrDefaultAsync(t => t.CharacterId == charId && t.ExecutionId == execId);
        Assert.NotNull(transition);
        Assert.Equal("ActivityOutcome", transition.SourceType);
    }

    [Fact]
    public async Task ContextLoader_LoadsAndEvolvesState_AutonomousDecisionPrioritizesNeeds()
    {
        var charId = Guid.NewGuid();
        var t0 = new DateTime(2026, 9, 3, 6, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddHours(12); // 12 hours later: Energy 80 -> 20 (< 30), Hunger 20 -> 68 (> 60)

        var character = new Character("Ronan", "Ranger", "avatar.png", "Quiet", "Hello", "Anime") { Id = charId };

        using var db = new CoreDbContext(_options);
        await db.Characters.AddAsync(character);
        await db.SaveChangesAsync();

        var stateTransitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, stateTransitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
        await stateService.GetOrCreateInitialStateAsync(charId, t0);

        var visualReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
        var contextLoader = new AutonomousCharacterContextLoader(db, visualReader, stateService, NullLogger<AutonomousCharacterContextLoader>.Instance);

        // Load context at t1
        var context = await contextLoader.LoadContextAsync(charId, t1);

        Assert.NotNull(context);
        Assert.NotNull(context.CurrentState);

        // Verify the state was evolved to t1
        Assert.Equal(t1, context.CurrentState.LastEvolvedAtUtc);
        Assert.True(context.CurrentState.Energy <= 30);
        Assert.True(context.CurrentState.Hunger >= 60);

        // Make autonomous decision
        var decisionService = new AutonomousDecisionService(NullLogger<AutonomousDecisionService>.Instance);
        var decisionRequest = new AutonomousDecisionRequest(
            CharacterId: charId,
            CurrentTime: t1,
            CurrentLocation: "Sanctuary",
            TimeBucket: "2026-09-03T16:00",
            StateSnapshot: context.CurrentState
        );

        var decision = await decisionService.DecideNextActionAsync(decisionRequest);

        Assert.NotNull(decision.Candidate);
        // Because Energy <= 30 and Hunger >= 60, high priority activities must be Rest, Relaxing, or Eating
        Assert.Contains(decision.Candidate.ActivityType, new[] { CharacterActivityType.Relaxing, CharacterActivityType.Sleeping, CharacterActivityType.Eating });
    }
}
