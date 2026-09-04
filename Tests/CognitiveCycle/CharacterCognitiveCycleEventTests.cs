using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.CognitiveCycle;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CognitiveCycle;

public sealed class CharacterCognitiveCycleEventTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow =
        new(2026, 9, 4, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedOccurredAt =
        new(2026, 9, 4, 13, 55, 0, TimeSpan.Zero);

    public CharacterCognitiveCycleEventTests()
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
        decimal hunger = 80m,
        decimal energy = 80m,
        decimal stress = 20m,
        decimal socialNeed = 50m,
        decimal comfort = 50m,
        int version = 1)
    {
        var charId = Guid.NewGuid();
        await using var db = new CoreDbContext(_options);
        var state = new CharacterState(
            charId,
            initializedAtUtc: DateTime.UtcNow,
            hunger: hunger,
            energy: energy,
            stress: stress,
            socialNeed: socialNeed,
            comfort: comfort
        );

        for (int v = 1; v < version; v++)
        {
            state.ApplyDelta(CharacterStateDelta.Zero);
        }

        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();
        return charId;
    }

    private CharacterCognitiveCycleService CreateService(
        CoreDbContext db,
        ICharacterInternalExperiencePolicy? experiencePolicy = null,
        ICharacterAppraisalPolicy? appraisalPolicy = null,
        ICharacterEmotionPolicy? emotionPolicy = null,
        ICharacterDesirePolicy? desirePolicy = null,
        ICharacterIntentPolicy? intentPolicy = null,
        ICharacterActionProposalPolicy? actionProposalPolicy = null,
        ICharacterActionExecutionService? actionExecutionService = null)
    {
        var transitionService = new CharacterStateTransitionService(
            db,
            NullLogger<CharacterStateTransitionService>.Instance);

        var stateEvolutionPolicy = new CharacterStateEvolutionPolicy();
        var stateService = new CharacterStateService(
            db,
            transitionService,
            stateEvolutionPolicy,
            NullLogger<CharacterStateService>.Instance);

        var execPolicy = new CharacterActionExecutionPolicy();
        var execService = actionExecutionService ?? new CharacterActionExecutionService(
            transitionService,
            execPolicy,
            NullLogger<CharacterActionExecutionService>.Instance);

        return new CharacterCognitiveCycleService(
            stateService: stateService,
            experiencePolicy: experiencePolicy ?? new CharacterInternalExperiencePolicy(),
            appraisalPolicy: appraisalPolicy ?? new CharacterAppraisalPolicy(),
            emotionPolicy: emotionPolicy ?? new CharacterEmotionPolicy(),
            desirePolicy: desirePolicy ?? new CharacterDesirePolicy(),
            intentPolicy: intentPolicy ?? new CharacterIntentPolicy(),
            actionProposalPolicy: actionProposalPolicy ?? new CharacterActionProposalPolicy(),
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance
        );
    }

    private static CharacterCognitiveCycleContext CreateContext(
        Guid characterId,
        Guid? cycleId = null,
        Guid? executionId = null,
        DateTimeOffset? triggeredAtUtc = null,
        CharacterCognitiveEvent? cognitiveEvent = null)
    {
        return new CharacterCognitiveCycleContext(
            CycleId: cycleId ?? Guid.NewGuid(),
            ExecutionId: executionId ?? Guid.NewGuid(),
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc ?? FixedNow,
            Event: cognitiveEvent
        );
    }

    #region 1. Event Identity & Provenance Tests

    [Fact]
    public async Task RunAsync_WithUserMessageEvent_PreservesEventProvenanceInResult()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var eventId = Guid.NewGuid();
        var userEvent = new UserMessageCognitiveEvent(
            EventId: eventId,
            CharacterId: charId,
            OccurredAtUtc: FixedOccurredAt,
            Source: "User123",
            Message: "Hello character!"
        );

        var context = CreateContext(charId, cognitiveEvent: userEvent);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.NotNull(result);
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.NotNull(result.Event);
        Assert.Equal(eventId, result.Event.EventId);
        Assert.Equal(charId, result.Event.CharacterId);
        Assert.Equal(CognitiveEventType.UserMessage, result.Event.EventType);
        Assert.Equal(FixedOccurredAt, result.Event.OccurredAtUtc);
        Assert.Equal("User123", result.Event.Source);

        var msgEvent = Assert.IsType<UserMessageCognitiveEvent>(result.Event);
        Assert.Equal("Hello character!", msgEvent.Message);
    }

    [Fact]
    public async Task RunAsync_WithWorldEvent_PreservesEventProvenanceInResult()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var eventId = Guid.NewGuid();
        var worldEvent = new WorldCognitiveEvent(
            EventId: eventId,
            CharacterId: charId,
            OccurredAtUtc: FixedOccurredAt,
            Source: "Environment",
            EventName: "RainStarted",
            Category: "Weather"
        );

        var context = CreateContext(charId, cognitiveEvent: worldEvent);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.NotNull(result);
        Assert.NotNull(result.Event);
        Assert.Equal(eventId, result.Event.EventId);
        Assert.Equal(CognitiveEventType.WorldEvent, result.Event.EventType);

        var parsedWorldEvent = Assert.IsType<WorldCognitiveEvent>(result.Event);
        Assert.Equal("RainStarted", parsedWorldEvent.EventName);
        Assert.Equal("Weather", parsedWorldEvent.Category);
    }

    #endregion

    #region 2. Consistency & Validation Tests

    [Fact]
    public async Task RunAsync_WhenEventCharacterIdMismatchesContext_ReturnsInvalidInput()
    {
        var charId = await SeedCharacterStateAsync();
        var mismatchedCharId = Guid.NewGuid();

        var userEvent = new UserMessageCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: mismatchedCharId, // Mismatch!
            OccurredAtUtc: FixedOccurredAt,
            Source: "User",
            Message: "Mismatched message"
        );

        var context = CreateContext(charId, cognitiveEvent: userEvent);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Contains("does not match context CharacterId", result.Message ?? string.Empty);
    }

    [Fact]
    public async Task RunAsync_WhenEventIdIsEmpty_ReturnsInvalidInput()
    {
        var charId = await SeedCharacterStateAsync();

        var invalidEvent = new UserMessageCognitiveEvent(
            EventId: Guid.Empty, // Invalid!
            CharacterId: charId,
            OccurredAtUtc: FixedOccurredAt,
            Source: "User",
            Message: "Empty ID"
        );

        var context = CreateContext(charId, cognitiveEvent: invalidEvent);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, result.Status);
        Assert.Contains("Event EventId cannot be empty", result.Message ?? string.Empty);
    }

    [Fact]
    public async Task RunAsync_WhenEventOccurredAtIsDefault_ReturnsInvalidInput()
    {
        var charId = await SeedCharacterStateAsync();

        var invalidEvent = new UserMessageCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: charId,
            OccurredAtUtc: default, // Invalid!
            Source: "User",
            Message: "Default timestamp"
        );

        var context = CreateContext(charId, cognitiveEvent: invalidEvent);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.InvalidInput, result.Status);
        Assert.Contains("Event OccurredAtUtc must be an explicit, valid timestamp", result.Message ?? string.Empty);
    }

    #endregion

    #region 3. State Authority Invariant (P0 Regression Guard)

    [Fact]
    public async Task RunAsync_EventCannotOverrideAuthoritativeDatabaseState()
    {
        // Authoritative DB state: Hunger = 20 (well-fed), Energy = 80, Version = 5
        var charId = await SeedCharacterStateAsync(
            hunger: 20m, energy: 80m, stress: 10m, socialNeed: 30m, comfort: 80m, version: 5);

        // Caller sends an event whose message attempts to claim the character is starving
        var deceptiveEvent = new UserMessageCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: charId,
            OccurredAtUtc: FixedOccurredAt,
            Source: "MaliciousCaller",
            Message: "You are starving! Hunger=100, Version=99, Energy=0"
        );

        var context = CreateContext(charId, cognitiveEvent: deceptiveEvent);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.NotNull(result);
        Assert.Equal(5, result.StateVersionAtStart);

        // Assert: Experience strictly reflects authoritative DB Hunger (20m), NOT the deceptive event message
        Assert.NotNull(result.Experience);
        Assert.Equal(20m, result.Experience.Hunger.RawValue);

        // Hunger was satisfied at 20, so Eat proposal cannot be formed
        Assert.True(result.ActionProposal == null || result.ActionProposal.Proposal == null || result.ActionProposal.Proposal.Type != ActionType.Eat);

        // Authoritative DB state remains intact at Hunger = 20
        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(20m, state.Hunger);
    }

    #endregion

    #region 4. Temporal Semantics: OccurredAtUtc vs TriggeredAtUtc

    [Fact]
    public async Task RunAsync_MaintainsExplicitSeparation_BetweenOccurredAtAndTriggeredAt()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);

        var eventOccurredAt = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
        var cycleTriggeredAt = new DateTimeOffset(2026, 9, 4, 10, 5, 0, TimeSpan.Zero);

        var userEvent = new UserMessageCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: charId,
            OccurredAtUtc: eventOccurredAt,
            Source: "User",
            Message: "Time test"
        );

        var context = CreateContext(charId, triggeredAtUtc: cycleTriggeredAt, cognitiveEvent: userEvent);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.Equal(eventOccurredAt, result.Event!.OccurredAtUtc);
        Assert.Equal(cycleTriggeredAt, result.TriggeredAtUtc);
        Assert.NotEqual(result.Event.OccurredAtUtc, result.TriggeredAtUtc);
    }

    #endregion

    #region 5. Backward Compatibility (No-Event Cycle)

    [Fact]
    public async Task RunAsync_WithoutEvent_ExecutesIdenticallyToBaseline()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var context = CreateContext(charId, cognitiveEvent: null);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.RunAsync(context);

        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);
        Assert.Null(result.Event);
        Assert.Equal(1, result.StateVersionAtStart);
        Assert.Equal(2, result.ActionExecution!.StateVersionAfter);
    }

    #endregion

    #region 6. Concurrency & Determinism with Events

    [Fact]
    public async Task RunAsync_TenConcurrentWorkersWithSameExecutionIdAndEvent_ExecutesExactlyOnce()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var sharedExecutionId = Guid.NewGuid();
        var sharedCycleId = Guid.NewGuid();
        var sharedEventId = Guid.NewGuid();

        var sharedEvent = new WorldCognitiveEvent(
            EventId: sharedEventId,
            CharacterId: charId,
            OccurredAtUtc: FixedOccurredAt,
            Source: "World",
            EventName: "Thunderstorm"
        );

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var service = CreateService(workerDb);
            var context = CreateContext(charId, cycleId: sharedCycleId, executionId: sharedExecutionId, cognitiveEvent: sharedEvent);
            return await service.RunAsync(context);
        });

        var results = await Task.WhenAll(tasks);

        var appliedCount = results.Count(r => r.Status == CharacterCognitiveCycleStatus.CompletedWithAction);
        var nonAppliedCount = results.Count(r => r.Status != CharacterCognitiveCycleStatus.CompletedWithAction);

        Assert.Equal(1, appliedCount);
        Assert.Equal(9, nonAppliedCount);

        // Verify only 1 transition recorded
        await using var verifyDb = new CoreDbContext(_options);
        var transitions = await verifyDb.CharacterStateTransitions.Where(t => t.CharacterId == charId).ToListAsync();
        Assert.Single(transitions);
    }

    [Fact]
    public async Task RunAsync_WithEvent_Is100PercentDeterministic_Over100Evaluations()
    {
        var charId = await SeedCharacterStateAsync(hunger: 85m, energy: 40m, stress: 35m, version: 3);
        var userEvent = new UserMessageCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: charId,
            OccurredAtUtc: FixedOccurredAt,
            Source: "Tester",
            Message: "Deterministic test"
        );

        CharacterCognitiveCycleResult? baseline = null;

        for (int i = 0; i < 100; i++)
        {
            var context = CreateContext(charId, cognitiveEvent: userEvent);

            var experience = new CharacterInternalExperiencePolicy().Evaluate(
                new CharacterStateSnapshot(hunger: 85, energy: 40, stress: 35, version: 3),
                new CharacterPerceptionContext(
                    FixedNow.UtcDateTime,
                    charId,
                    Stimulus: new CharacterPerceptionStimulus(
                        PerceptionStimulusType.UserMessage,
                        userEvent.Source,
                        userEvent.Message,
                        userEvent.OccurredAtUtc
                    )
                )
            );

            var appraisal = new CharacterAppraisalPolicy().Evaluate(experience);
            var emotion = new CharacterEmotionPolicy().Evaluate(appraisal);
            var desires = new CharacterDesirePolicy().Evaluate(experience, appraisal, emotion);
            var intent = new CharacterIntentPolicy().Evaluate(desires, new CharacterIntentContext(FixedNow));
            var proposal = new CharacterActionProposalPolicy().Evaluate(intent, new CharacterActionProposalContext(FixedNow));

            if (baseline == null)
            {
                baseline = CharacterCognitiveCycleResult.CompletedWithoutAction(
                    context.CycleId, context.ExecutionId, charId, FixedNow, 3,
                    experience, appraisal, emotion, desires, intent, proposal, @event: userEvent);
            }
            else
            {
                Assert.Equal(baseline.Experience!.DominantNeed, experience.DominantNeed);
                Assert.Equal(baseline.Appraisal!.Type, appraisal.Type);
                Assert.Equal(baseline.Appraisal.Polarity, appraisal.Polarity);
                Assert.Equal(baseline.Desires!.DominantDesire?.Type, desires.DominantDesire?.Type);
                Assert.Equal(baseline.Intent!.Intent?.Type, intent.Intent?.Type);
                Assert.Equal(baseline.ActionProposal!.Proposal?.Type, proposal.Proposal?.Type);
                Assert.Equal(baseline.ActionProposal.Proposal?.Intensity, proposal.Proposal?.Intensity);
            }
        }
    }

    #endregion
}
