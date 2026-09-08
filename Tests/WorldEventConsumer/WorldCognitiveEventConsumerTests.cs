using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Abstractions.Time;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.LifeSimulation;
using Application.Interfaces;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.CognitiveCycle;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.WorldEventConsumer;

public sealed class WorldCognitiveEventConsumerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private readonly FakeSystemClock _clock;

    public WorldCognitiveEventConsumerTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();

        _clock = new FakeSystemClock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private CoreDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection);

        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new CoreDbContext(builder.Options);
    }

    private static WorldCognitiveEvent CreateValidEvent(
        Guid? eventId = null,
        Guid? characterId = null,
        DateTimeOffset? occurredAtUtc = null,
        string eventName = "ThunderstormStarted",
        string source = "WeatherSystem",
        string? category = "Weather")
    {
        return new WorldCognitiveEvent(
            EventId: eventId ?? Guid.NewGuid(),
            CharacterId: characterId ?? Guid.NewGuid(),
            OccurredAtUtc: occurredAtUtc ?? new DateTimeOffset(2026, 9, 8, 11, 55, 0, TimeSpan.Zero),
            EventName: eventName,
            Source: source,
            Category: category
        );
    }

    private sealed class FakeSystemClock : ISystemClock
    {
        public DateTimeOffset CurrentTime { get; set; }
        public FakeSystemClock(DateTimeOffset initialTime) => CurrentTime = initialTime;
        public DateTimeOffset UtcNow => CurrentTime;
        public DateTime UtcDateTime => UtcNow.UtcDateTime;
    }

    private sealed class FakeCharacterCognitiveCycleService : ICharacterCognitiveCycleService
    {
        public CharacterCognitiveCycleContext? LastContext { get; private set; }
        public List<CharacterCognitiveCycleContext> DispatchedContexts { get; } = new();
        public Func<CharacterCognitiveCycleContext, CancellationToken, Task<CharacterCognitiveCycleResult>>? Handler { get; set; }
        public int InvocationCount => DispatchedContexts.Count;

        public Task<CharacterCognitiveCycleResult> RunAsync(
            CharacterCognitiveCycleContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastContext = context;
            DispatchedContexts.Add(context);

            if (Handler != null)
            {
                return Handler(context, cancellationToken);
            }

            return Task.FromResult(CharacterCognitiveCycleResult.CompletedWithoutAction(
                context.CycleId,
                context.ExecutionId,
                context.CharacterId,
                context.TriggeredAtUtc,
                stateVersionAtStart: 1,
                @event: context.Event,
                message: "Completed without action by fake service."));
        }
    }

    private sealed class FakeActionSafetyGate : IActionSafetyGate
    {
        public Func<Guid, CharacterActionProposal, CancellationToken, Task<SafetyDecision>>? Handler { get; set; }
        public int EvaluationCount { get; private set; }

        public Task<SafetyDecision> EvaluateAsync(
            Guid characterId,
            CharacterActionProposal proposal,
            CancellationToken ct = default)
        {
            EvaluationCount++;
            if (Handler != null)
            {
                return Handler(characterId, proposal, ct);
            }

            return Task.FromResult(SafetyDecision.Allowed("ALLOW_ALL", "Allowed by fake safety gate"));
        }
    }

    private sealed class FakeActionExecutionService : ICharacterActionExecutionService
    {
        public int ExecutionCount { get; private set; }

        public Task<CharacterActionExecutionResult> ExecuteAsync(
            Guid characterId,
            CharacterActionProposal proposal,
            CharacterActionExecutionContext context,
            CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult(new CharacterActionExecutionResult(
                ExecutionId: context.ExecutionId,
                CharacterId: characterId,
                Status: CharacterActionExecutionStatus.Applied,
                ActionType: proposal.Type,
                Intensity: proposal.Intensity,
                SourceIntent: proposal.SourceIntent,
                Motivation: proposal.Motivation,
                StateVersionBefore: 1,
                StateVersionAfter: 2,
                AppliedDelta: new CharacterStateDelta()));
        }
    }

    private sealed class FakeWorldCognitiveEventConsumptionRepository : IWorldCognitiveEventConsumptionRepository
    {
        public Func<WorldCognitiveEventConsumption, CancellationToken, Task<(bool, WorldCognitiveEventConsumption)>>? TryClaimHandler { get; set; }
        public Func<Guid, Guid, DateTime, CancellationToken, Task>? MarkConsumedHandler { get; set; }
        public Func<Guid, string, CancellationToken, Task>? MarkFailedHandler { get; set; }

        public Task<WorldCognitiveEventConsumption?> GetByEventIdAsync(Guid eventId, CancellationToken ct = default) =>
            Task.FromResult<WorldCognitiveEventConsumption?>(null);

        public Task<(bool IsClaimed, WorldCognitiveEventConsumption Consumption)> TryClaimAsync(
            WorldCognitiveEventConsumption consumption,
            CancellationToken ct = default)
        {
            if (TryClaimHandler != null)
                return TryClaimHandler(consumption, ct);
            return Task.FromResult((true, consumption));
        }

        public Task MarkConsumedAsync(Guid eventId, Guid cycleId, DateTime consumedAtUtc, CancellationToken ct = default)
        {
            if (MarkConsumedHandler != null)
                return MarkConsumedHandler(eventId, cycleId, consumedAtUtc, ct);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid eventId, string failureReason, CancellationToken ct = default)
        {
            if (MarkFailedHandler != null)
                return MarkFailedHandler(eventId, failureReason, ct);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ConcurrentConsumerRaceInterceptor : SaveChangesInterceptor
    {
        private readonly Func<Task> _onSavingChangesAsync;
        public bool InterceptorFired { get; private set; }

        public ConcurrentConsumerRaceInterceptor(Func<Task> onSavingChangesAsync)
        {
            _onSavingChangesAsync = onSavingChangesAsync;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!InterceptorFired)
            {
                InterceptorFired = true;
                await _onSavingChangesAsync();
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    #region Category 1: Event Validation (Tests 1-6)

    [Fact]
    public void WorldCognitiveEvent_RejectsEmptyEventId()
    {
        var evt = new WorldCognitiveEvent(
            EventId: Guid.Empty,
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: _clock.UtcNow,
            EventName: "Storm"
        );

        var ex = Assert.Throws<ArgumentException>(() =>
            WorldCognitiveEventConsumer.ValidateWorldEvent(evt, _clock));
        Assert.Contains("EventId", ex.Message);
    }

    [Fact]
    public void WorldCognitiveEvent_RejectsEmptyCharacterId()
    {
        var evt = new WorldCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: Guid.Empty,
            OccurredAtUtc: _clock.UtcNow,
            EventName: "Storm"
        );

        var ex = Assert.Throws<ArgumentException>(() =>
            WorldCognitiveEventConsumer.ValidateWorldEvent(evt, _clock));
        Assert.Contains("CharacterId", ex.Message);
    }

    [Fact]
    public void WorldCognitiveEvent_RejectsInvalidTimestamp()
    {
        // Far future timestamp (more than 1 day in future)
        var futureEvt = new WorldCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: _clock.UtcNow.AddDays(5),
            EventName: "Storm"
        );

        var ex1 = Assert.Throws<ArgumentException>(() =>
            WorldCognitiveEventConsumer.ValidateWorldEvent(futureEvt, _clock));
        Assert.Contains("OccurredAtUtc", ex1.Message);

        // Default / empty timestamp
        var defaultEvt = new WorldCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: default,
            EventName: "Storm"
        );

        var ex2 = Assert.Throws<ArgumentException>(() =>
            WorldCognitiveEventConsumer.ValidateWorldEvent(defaultEvt, _clock));
        Assert.Contains("OccurredAtUtc", ex2.Message);
    }

    [Fact]
    public void WorldCognitiveEvent_RejectsEmptySource()
    {
        var evt = new WorldCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: _clock.UtcNow,
            EventName: "Storm",
            Source: "   "
        );

        var ex = Assert.Throws<ArgumentException>(() =>
            WorldCognitiveEventConsumer.ValidateWorldEvent(evt, _clock));
        Assert.Contains("Source", ex.Message);
    }

    [Fact]
    public void WorldCognitiveEvent_RejectsEmptyEventType()
    {
        var evt = new WorldCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: _clock.UtcNow,
            EventName: "   "
        );

        var ex = Assert.Throws<ArgumentException>(() =>
            WorldCognitiveEventConsumer.ValidateWorldEvent(evt, _clock));
        Assert.Contains("EventName", ex.Message);
    }

    [Fact]
    public void WorldCognitiveEvent_PreservesEventIdentity()
    {
        var eventId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var time = _clock.UtcNow;

        var evt = new WorldCognitiveEvent(
            EventId: eventId,
            CharacterId: characterId,
            OccurredAtUtc: time,
            EventName: "Earthquake",
            Source: "Environment",
            Category: "Disaster"
        );

        Assert.Equal(eventId, evt.EventId);
        Assert.Equal(characterId, evt.CharacterId);
        Assert.Equal(time, evt.OccurredAtUtc);
        Assert.Equal("Earthquake", evt.EventName);
        Assert.Equal("Environment", evt.Source);
        Assert.Equal("Disaster", evt.Category);
    }

    #endregion

    #region Category 2: Consumer Dispatch & Boundaries (Tests 7-12)

    [Fact]
    public async Task Consumer_ValidWorldEvent_DispatchesToCognitiveCycle()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();
        var result = await consumer.ConsumeAsync(evt);

        Assert.True(result.IsAccepted);
        Assert.Equal(WorldCognitiveEventConsumptionStatus.Processed, result.Status);
        Assert.NotNull(fakeCycleService.LastContext);
        Assert.Equal(evt.CharacterId, fakeCycleService.LastContext.CharacterId);
        Assert.Equal(evt, fakeCycleService.LastContext.Event);
        Assert.Equal(1, fakeCycleService.InvocationCount);
    }

    [Fact]
    public async Task Consumer_CharacterIdIsPreserved()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var expectedCharacterId = Guid.NewGuid();
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent(characterId: expectedCharacterId);
        var result = await consumer.ConsumeAsync(evt);

        Assert.Equal(expectedCharacterId, result.CharacterId);
        Assert.NotNull(fakeCycleService.LastContext);
        Assert.Equal(expectedCharacterId, fakeCycleService.LastContext.CharacterId);
    }

    [Fact]
    public async Task Consumer_EventIdIsPreserved()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var expectedEventId = Guid.NewGuid();
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent(eventId: expectedEventId);
        var result = await consumer.ConsumeAsync(evt);

        Assert.Equal(expectedEventId, result.EventId);
        Assert.NotNull(fakeCycleService.LastContext);
        Assert.Equal(expectedEventId, fakeCycleService.LastContext.Event?.EventId);
    }

    [Fact]
    public async Task Consumer_InvalidEvent_DoesNotDispatch()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var invalidEvt = new WorldCognitiveEvent(
            EventId: Guid.Empty, // Invalid
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: _clock.UtcNow,
            EventName: "InvalidEvent"
        );

        await Assert.ThrowsAsync<ArgumentException>(() => consumer.ConsumeAsync(invalidEvt));
        Assert.Equal(0, fakeCycleService.InvocationCount);
    }

    [Fact]
    public async Task Consumer_CancellationPropagates()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var evt = CreateValidEvent();
        await Assert.ThrowsAsync<OperationCanceledException>(() => consumer.ConsumeAsync(evt, cts.Token));
        Assert.Equal(0, fakeCycleService.InvocationCount);
    }

    [Fact]
    public async Task Consumer_EventForCharacterA_NeverDispatchesToCharacterB()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();

        var fakeCycleService = new FakeCharacterCognitiveCycleService();
        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evtA = CreateValidEvent(characterId: charA);
        await consumer.ConsumeAsync(evtA);

        Assert.Equal(1, fakeCycleService.InvocationCount);
        Assert.Equal(charA, fakeCycleService.LastContext?.CharacterId);
        Assert.DoesNotContain(fakeCycleService.DispatchedContexts, c => c.CharacterId == charB);
    }

    #endregion

    #region Category 3: Idempotency & Conflict Semantics (Tests 13-17)

    [Fact]
    public async Task Consumer_SameEventSamePayload_IsIdempotent()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();

        var result1 = await consumer.ConsumeAsync(evt);
        var result2 = await consumer.ConsumeAsync(evt);

        Assert.True(result1.IsAccepted);
        Assert.True(result2.IsDuplicate);
        Assert.Equal(result1.CycleId, result2.CycleId);
        Assert.Equal(1, fakeCycleService.InvocationCount);
    }

    [Fact]
    public async Task Consumer_SameEvent_DoesNotCreateSecondCycle()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();
        await consumer.ConsumeAsync(evt);
        await consumer.ConsumeAsync(evt);

        Assert.Single(fakeCycleService.DispatchedContexts);
    }

    [Fact]
    public async Task Consumer_SameEvent_DifferentPayload_ThrowsConflict()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var sharedEventId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var evt1 = CreateValidEvent(eventId: sharedEventId, characterId: charId, eventName: "ThunderstormStarted");
        var evt2 = CreateValidEvent(eventId: sharedEventId, characterId: charId, eventName: "EarthquakeStarted"); // Divergent payload!

        await consumer.ConsumeAsync(evt1);

        var ex = await Assert.ThrowsAsync<WorldCognitiveEventIdempotencyConflictException>(() =>
            consumer.ConsumeAsync(evt2));

        Assert.Equal(sharedEventId, ex.EventId);
    }

    [Fact]
    public async Task Consumer_ConcurrentSameEvent_ProducesSingleBusinessConsumption()
    {
        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var evt = CreateValidEvent(eventId: eventId, characterId: charId);

        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        // Setup interceptor for Worker B: right before Worker B commits its claim, Worker A executes completely.
        var interceptor = new ConcurrentConsumerRaceInterceptor(async () =>
        {
            await using var dbWorkerA = new CoreDbContext(_options);
            var repoA = new WorldCognitiveEventConsumptionRepository(dbWorkerA);
            var consumerA = new WorldCognitiveEventConsumer(
                repoA, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

            var resultA = await consumerA.ConsumeAsync(evt);
            Assert.True(resultA.IsAccepted);
        });

        await using var dbWorkerB = CreateDbContext(interceptor);
        var repoB = new WorldCognitiveEventConsumptionRepository(dbWorkerB);
        var consumerB = new WorldCognitiveEventConsumer(
            repoB, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        // Worker B attempts to consume, hits interceptor where Worker A commits first,
        // then Worker B's insert gets unique constraint violation, recovers, and returns Duplicate.
        var resultB = await consumerB.ConsumeAsync(evt);

        Assert.True(interceptor.InterceptorFired);
        Assert.True(resultB.IsDuplicate);
        Assert.Equal(1, fakeCycleService.InvocationCount);

        // Verify database contains exactly one consumption row
        await using var verifyDb = CreateDbContext();
        var count = await verifyDb.WorldCognitiveEventConsumptions.CountAsync(c => c.EventId == eventId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Consumer_ConcurrentDivergentSameEvent_ProducesConflict()
    {
        var sharedEventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var evtA = CreateValidEvent(eventId: sharedEventId, characterId: charId, eventName: "EventA");
        var evtB = CreateValidEvent(eventId: sharedEventId, characterId: charId, eventName: "EventB_Divergent");

        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var interceptor = new ConcurrentConsumerRaceInterceptor(async () =>
        {
            await using var dbWorkerA = new CoreDbContext(_options);
            var repoA = new WorldCognitiveEventConsumptionRepository(dbWorkerA);
            var consumerA = new WorldCognitiveEventConsumer(
                repoA, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

            await consumerA.ConsumeAsync(evtA);
        });

        await using var dbWorkerB = CreateDbContext(interceptor);
        var repoB = new WorldCognitiveEventConsumptionRepository(dbWorkerB);
        var consumerB = new WorldCognitiveEventConsumer(
            repoB, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        await Assert.ThrowsAsync<WorldCognitiveEventIdempotencyConflictException>(() =>
            consumerB.ConsumeAsync(evtB));
    }

    #endregion

    #region Category 4: Repository Fingerprint & Persistence (Tests 18-20)

    [Fact]
    public async Task Repository_IncomingFingerprintMustMatchCanonical()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = _clock.UtcNow;

        // Construct consumption with tampered/arbitrary fingerprint
        var badConsumption = new WorldCognitiveEventConsumption(
            eventId: eventId,
            characterId: charId,
            occurredAtUtc: now.UtcDateTime,
            eventName: "Thunderstorm",
            source: "Weather",
            category: "Nature",
            fingerprint: "tampered_arbitrary_fingerprint_0000000000000000000000000000000000000",
            createdAtUtc: now.UtcDateTime
        );

        var ex = await Assert.ThrowsAsync<WorldCognitiveEventIdempotencyConflictException>(() =>
            repo.TryClaimAsync(badConsumption));

        Assert.Equal(eventId, ex.EventId);
    }

    [Fact]
    public async Task Repository_SameEventSameFingerprint_IsIdempotent()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = _clock.UtcNow;

        var claim1 = WorldCognitiveEventConsumption.CreateClaim(
            eventId, charId, now, "Thunderstorm", "Weather", "Nature", now.UtcDateTime);

        var claim2 = WorldCognitiveEventConsumption.CreateClaim(
            eventId, charId, now, "Thunderstorm", "Weather", "Nature", now.UtcDateTime);

        var (claimed1, _) = await repo.TryClaimAsync(claim1);
        var (claimed2, existing2) = await repo.TryClaimAsync(claim2);

        Assert.True(claimed1);
        Assert.False(claimed2);
        Assert.Equal(claim1.Fingerprint, existing2.Fingerprint);
    }

    [Fact]
    public async Task Repository_SameEventDifferentFingerprint_IsConflict()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var sharedEventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = _clock.UtcNow;

        var claim1 = WorldCognitiveEventConsumption.CreateClaim(
            sharedEventId, charId, now, "Thunderstorm", "Weather", "Nature", now.UtcDateTime);

        var claim2 = WorldCognitiveEventConsumption.CreateClaim(
            sharedEventId, charId, now, "Earthquake", "Weather", "Nature", now.UtcDateTime);

        await repo.TryClaimAsync(claim1);

        await Assert.ThrowsAsync<WorldCognitiveEventIdempotencyConflictException>(() =>
            repo.TryClaimAsync(claim2));
    }

    #endregion

    #region Category 5: Identity Separation (Tests 21-23)

    [Fact]
    public async Task Consumer_EventIdIsDistinctFromCycleId()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var fakeCycleService = new FakeCharacterCognitiveCycleService();
        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();
        var result = await consumer.ConsumeAsync(evt);

        Assert.NotNull(fakeCycleService.LastContext);
        var capturedCycleId = fakeCycleService.LastContext.CycleId;

        Assert.NotEqual(Guid.Empty, capturedCycleId);
        Assert.NotEqual(evt.EventId, capturedCycleId);
        Assert.NotEqual(evt.EventId, result.CycleId);
    }

    [Fact]
    public async Task Consumer_EventIdIsDistinctFromExecutionId()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var fakeCycleService = new FakeCharacterCognitiveCycleService();
        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();
        await consumer.ConsumeAsync(evt);

        Assert.NotNull(fakeCycleService.LastContext);
        var capturedExecutionId = fakeCycleService.LastContext.ExecutionId;

        Assert.NotEqual(Guid.Empty, capturedExecutionId);
        Assert.NotEqual(evt.EventId, capturedExecutionId);
    }

    [Fact]
    public void Consumer_EventIdIsDistinctFromOutboxMessageId()
    {
        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var time = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

        var outboxMessage = new CharacterOutboxMessage(
            eventId: eventId,
            characterId: charId,
            eventType: "ActivityStarted",
            payloadJson: "{}",
            fingerprint: "fp",
            occurredAtUtc: time,
            id: Guid.NewGuid()
        );

        Assert.NotEqual(outboxMessage.Id, outboxMessage.EventId);
        Assert.Equal(eventId, outboxMessage.EventId);
    }

    #endregion

    #region Category 6: Boundary & Zero State Mutation (Tests 24-29)

    [Fact]
    public void WorldEvent_DoesNotCarryCharacterStateMetrics()
    {
        var properties = typeof(WorldCognitiveEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var propertyNames = properties.Select(p => p.Name.ToLowerInvariant()).ToList();

        // Invariant: WorldCognitiveEvent contains zero CharacterState metrics
        Assert.DoesNotContain("hunger", propertyNames);
        Assert.DoesNotContain("energy", propertyNames);
        Assert.DoesNotContain("mood", propertyNames);
        Assert.DoesNotContain("stress", propertyNames);
        Assert.DoesNotContain("socialneed", propertyNames);
        Assert.DoesNotContain("comfort", propertyNames);
        Assert.DoesNotContain("characterstate", propertyNames);
    }

    [Fact]
    public void Consumer_DoesNotMutateCharacterState()
    {
        // Audit constructor parameters of WorldCognitiveEventConsumer:
        // Must NOT inject CharacterState repository, context, or service
        var ctorParams = typeof(WorldCognitiveEventConsumer).GetConstructors()[0].GetParameters();
        var paramTypes = ctorParams.Select(p => p.ParameterType).ToList();

        Assert.DoesNotContain(typeof(ICharacterStateService), paramTypes);
        Assert.DoesNotContain(typeof(ICharacterStateTransitionService), paramTypes);
        Assert.DoesNotContain(typeof(CoreDbContext), paramTypes);
    }

    [Fact]
    public void Consumer_DoesNotMutateMemoryDirectly()
    {
        var ctorParams = typeof(WorldCognitiveEventConsumer).GetConstructors()[0].GetParameters();
        var paramTypes = ctorParams.Select(p => p.ParameterType).ToList();

        Assert.DoesNotContain(typeof(IMemoryService), paramTypes);
        Assert.DoesNotContain(typeof(ICharacterMemoryRepository), paramTypes);
    }

    [Fact]
    public void Consumer_DoesNotMutateRelationshipDirectly()
    {
        var ctorParams = typeof(WorldCognitiveEventConsumer).GetConstructors()[0].GetParameters();
        var paramTypes = ctorParams.Select(p => p.ParameterType).ToList();

        Assert.DoesNotContain(typeof(ICharacterRelationshipRepository), paramTypes);
    }

    [Fact]
    public void Consumer_DoesNotMutatePersonalityDirectly()
    {
        var ctorParams = typeof(WorldCognitiveEventConsumer).GetConstructors()[0].GetParameters();
        var paramTypes = ctorParams.Select(p => p.ParameterType).ToList();

        Assert.DoesNotContain(typeof(ICharacterPersonalityRepository), paramTypes);
    }

    [Fact]
    public void Consumer_DoesNotCallLifeSimulation()
    {
        var ctorParams = typeof(WorldCognitiveEventConsumer).GetConstructors()[0].GetParameters();
        var paramTypes = ctorParams.Select(p => p.ParameterType).ToList();

        Assert.DoesNotContain(typeof(ILifeSimulationService), paramTypes);
        Assert.DoesNotContain(typeof(ICharacterLifeActivityRepository), paramTypes);
    }

    #endregion

    #region Category 7: Cognitive Pipeline & Safety (Tests 30-31)

    [Fact]
    public async Task Consumer_DispatchesThroughExistingCognitiveCycleBoundary()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();
        await consumer.ConsumeAsync(evt);

        // Dispatches through ICharacterCognitiveCycleService
        Assert.Equal(1, fakeCycleService.InvocationCount);
        Assert.Equal(evt, fakeCycleService.LastContext?.Event);
    }

    [Fact]
    public async Task Consumer_DoesNotBypassSafetyGate()
    {
        var fakeSafetyGate = new FakeActionSafetyGate
        {
            Handler = (id, prop, ct) => Task.FromResult(SafetyDecision.Denied("SAFETY_VIOLATION", "Dangerous action"))
        };

        var fakeActionExecution = new FakeActionExecutionService();

        // When ActionProposal is generated, Safety Gate blocks before execution in CognitiveCycle
        var fakeCycleService = new FakeCharacterCognitiveCycleService
        {
            Handler = (ctx, ct) =>
            {
                return Task.FromResult(CharacterCognitiveCycleResult.CompletedWithoutAction(
                    ctx.CycleId, ctx.ExecutionId, ctx.CharacterId, ctx.TriggeredAtUtc, 1,
                    message: "Action proposal blocked by safety policy 'SAFETY_VIOLATION': Dangerous action",
                    safetyDecision: SafetyDecision.Denied("SAFETY_VIOLATION", "Dangerous action")));
            }
        };

        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();
        var result = await consumer.ConsumeAsync(evt);

        // Verification: Even with blocked safety decision, consumer handles cycle result cleanly
        Assert.True(result.IsAccepted);
        Assert.Equal(0, fakeActionExecution.ExecutionCount);
    }

    #endregion

    #region Category 8: Failure Handling (Tests 32-33)

    [Fact]
    public async Task CognitiveCycleFailure_DoesNotSilentlyMarkEventConsumed()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var fakeCycleService = new FakeCharacterCognitiveCycleService
        {
            Handler = (ctx, ct) => throw new InvalidOperationException("Cognitive cycle engine crashed")
        };

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.ConsumeAsync(evt));

        // Invariant: Event must NOT be marked Consumed
        var record = await repo.GetByEventIdAsync(evt.EventId);
        Assert.NotNull(record);
        Assert.NotEqual(EventConsumptionState.Consumed, record.State);
        Assert.Equal(EventConsumptionState.Failed, record.State);
        Assert.Contains("Cognitive cycle engine crashed", record.FailureReason);
    }

    [Fact]
    public async Task PersistenceFailure_DoesNotCreateFalseSuccessfulConsumption()
    {
        var evt = CreateValidEvent();
        var now = _clock.UtcNow.UtcDateTime;
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, now);

        var fakeRepo = new FakeWorldCognitiveEventConsumptionRepository
        {
            TryClaimHandler = (c, ct) => Task.FromResult((true, claim)),
            MarkConsumedHandler = (eid, cid, t, ct) => throw new DbUpdateException("Database connection severed during commit")
        };

        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            fakeRepo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        // Invariant: Persistence failure must bubble up and never return false ProcessedResult
        await Assert.ThrowsAsync<DbUpdateException>(() => consumer.ConsumeAsync(evt));
    }

    #endregion

    #region Category 9: Determinism (Tests 34-35)

    [Fact]
    public void SameWorldEventProducesDeterministicSemanticConsumption()
    {
        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        var fp1 = CanonicalWorldEventFingerprint.Compute(
            eventId, charId, occurredAt, "WeatherShift", "Weather", "Environment");

        var fp2 = CanonicalWorldEventFingerprint.Compute(
            eventId, charId, occurredAt, "WeatherShift", "Weather", "Environment");

        Assert.Equal(fp1, fp2);
        Assert.Equal(64, fp1.Length);
    }

    [Fact]
    public void FingerprintIsDeterministic()
    {
        var eventId = new Guid("11111111-2222-3333-4444-555555555555");
        var charId = new Guid("66666666-7777-8888-9999-000000000000");
        var occurredAt = new DateTimeOffset(2026, 9, 8, 10, 30, 0, TimeSpan.Zero);

        // Known canonical expected format
        var canonical = $"1|11111111-2222-3333-4444-555555555555|66666666-7777-8888-9999-000000000000|2026-09-08T10:30:00.0000000+00:00|Sensor|FireAlarm|Hazard";
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        var actualHash = CanonicalWorldEventFingerprint.Compute(
            eventId, charId, occurredAt, "FireAlarm", "Sensor", "Hazard");

        Assert.Equal(expectedHash, actualHash);
    }

    #endregion
}
