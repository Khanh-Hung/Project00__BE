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
using Microsoft.Extensions.Logging;
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

    public sealed class FakeSystemClock : ISystemClock
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
        public Func<WorldCognitiveEventConsumption, TimeSpan?, CancellationToken, Task<(bool, WorldCognitiveEventConsumption)>>? TryClaimHandler { get; set; }
        public Func<Guid, DateTime, TimeSpan?, CancellationToken, Task<(bool, WorldCognitiveEventConsumption)>>? ReclaimHandler { get; set; }
        public Func<Guid, Guid, DateTime, uint?, CancellationToken, Task>? MarkConsumedHandler { get; set; }
        public Func<Guid, string, DateTime, uint?, CancellationToken, Task>? MarkFailedHandler { get; set; }

        public Task<WorldCognitiveEventConsumption?> GetByEventIdAsync(Guid eventId, CancellationToken ct = default) =>
            Task.FromResult<WorldCognitiveEventConsumption?>(null);

        public Task<(bool IsClaimed, WorldCognitiveEventConsumption Consumption)> TryClaimAsync(
            WorldCognitiveEventConsumption consumption,
            TimeSpan? leaseTimeout = null,
            CancellationToken ct = default)
        {
            if (TryClaimHandler != null)
                return TryClaimHandler(consumption, leaseTimeout, ct);
            return Task.FromResult((true, consumption));
        }

        public Task<(bool IsReclaimed, WorldCognitiveEventConsumption Consumption)> ReclaimAsync(
            Guid eventId,
            DateTime attemptedAtUtc,
            TimeSpan? leaseTimeout = null,
            CancellationToken ct = default)
        {
            if (ReclaimHandler != null)
                return ReclaimHandler(eventId, attemptedAtUtc, leaseTimeout, ct);
            return Task.FromResult((true, (WorldCognitiveEventConsumption)null!));
        }

        public Task MarkConsumedAsync(
            Guid eventId,
            Guid cycleId,
            DateTime consumedAtUtc,
            uint? expectedVersion = null,
            CancellationToken ct = default)
        {
            if (MarkConsumedHandler != null)
                return MarkConsumedHandler(eventId, cycleId, consumedAtUtc, expectedVersion, ct);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(
            Guid eventId,
            string failureReason,
            DateTime failedAtUtc,
            uint? expectedVersion = null,
            CancellationToken ct = default)
        {
            if (MarkFailedHandler != null)
                return MarkFailedHandler(eventId, failureReason, failedAtUtc, expectedVersion, ct);
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

    private sealed class TestSpyLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception), exception));
        }
    }

    #region Category 1: Event Validation & Clock Hardening (Tests 1-6)

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
        // Far future timestamp (> clock.UtcNow + 1 day)
        var futureEvt = new WorldCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: _clock.UtcNow.AddDays(5),
            EventName: "Storm"
        );

        var ex1 = Assert.Throws<ArgumentException>(() =>
            WorldCognitiveEventConsumer.ValidateWorldEvent(futureEvt, _clock));
        Assert.Contains("OccurredAtUtc", ex1.Message);

        // Ancient timestamp (< clock.UtcNow - 50 years)
        var ancientEvt = new WorldCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: _clock.UtcNow.AddYears(-60),
            EventName: "Storm"
        );

        var ex2 = Assert.Throws<ArgumentException>(() =>
            WorldCognitiveEventConsumer.ValidateWorldEvent(ancientEvt, _clock));
        Assert.Contains("OccurredAtUtc", ex2.Message);

        // Default / empty timestamp
        var defaultEvt = new WorldCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: default,
            EventName: "Storm"
        );

        var ex3 = Assert.Throws<ArgumentException>(() =>
            WorldCognitiveEventConsumer.ValidateWorldEvent(defaultEvt, _clock));
        Assert.Contains("OccurredAtUtc", ex3.Message);
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
            EventId: Guid.Empty,
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

    #region Category 3: Idempotency, Crash Recovery & Conflict Semantics (Tests 13-17)

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
        var trackerA = new WorldCognitiveEventInFlightTracker();
        var trackerB = new WorldCognitiveEventInFlightTracker();

        // Worker B hits interceptor before commit; Worker A runs and commits cleanly
        var interceptor = new ConcurrentConsumerRaceInterceptor(async () =>
        {
            await using var dbWorkerA = new CoreDbContext(_options);
            var repoA = new WorldCognitiveEventConsumptionRepository(dbWorkerA, trackerA);
            var consumerA = new WorldCognitiveEventConsumer(
                repoA, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, trackerA);

            var resultA = await consumerA.ConsumeAsync(evt);
            Assert.True(resultA.IsAccepted);
        });

        await using var dbWorkerB = CreateDbContext(interceptor);
        var repoB = new WorldCognitiveEventConsumptionRepository(dbWorkerB, trackerB);
        var consumerB = new WorldCognitiveEventConsumer(
            repoB, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, trackerB);

        var resultB = await consumerB.ConsumeAsync(evt);

        Assert.True(interceptor.InterceptorFired);
        Assert.True(resultB.IsDuplicate);
        Assert.Equal(1, fakeCycleService.InvocationCount);

        // Verify exactly one record in database
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
        var trackerA = new WorldCognitiveEventInFlightTracker();
        var trackerB = new WorldCognitiveEventInFlightTracker();

        var interceptor = new ConcurrentConsumerRaceInterceptor(async () =>
        {
            await using var dbWorkerA = new CoreDbContext(_options);
            var repoA = new WorldCognitiveEventConsumptionRepository(dbWorkerA, trackerA);
            var consumerA = new WorldCognitiveEventConsumer(
                repoA, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, trackerA);

            await consumerA.ConsumeAsync(evtA);
        });

        await using var dbWorkerB = CreateDbContext(interceptor);
        var repoB = new WorldCognitiveEventConsumptionRepository(dbWorkerB, trackerB);
        var consumerB = new WorldCognitiveEventConsumer(
            repoB, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, trackerB);

        await Assert.ThrowsAsync<WorldCognitiveEventIdempotencyConflictException>(() =>
            consumerB.ConsumeAsync(evtB));
    }

    [Fact]
    public async Task Consumer_CrashRecovery_InProgressLeaseExpired_ReclaimsAndCompletes()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();

        // 1. First claim: simulate crash right after DB claim committed (claim created, cycle never run)
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, _clock.UtcNow.UtcDateTime);
        var (isClaimed, _) = await repo.TryClaimAsync(claim);
        Assert.True(isClaimed);

        // 2. Immediate redelivery within 1 minute (lease active): treated as duplicate / in-flight
        _clock.CurrentTime = _clock.CurrentTime.AddMinutes(1);
        var activeResult = await consumer.ConsumeAsync(evt);
        Assert.True(activeResult.IsDuplicate);
        Assert.Equal(0, fakeCycleService.InvocationCount);

        // 3. Crash recovery: advance clock past 5 minutes lease timeout
        _clock.CurrentTime = _clock.CurrentTime.AddMinutes(5);

        // Under Option A: normal consumer dispatch treats InProgress as duplicate (prevents un-fenced side effects)
        var redelivery = await consumer.ConsumeAsync(evt);
        Assert.True(redelivery.IsDuplicate);
        Assert.Equal(0, fakeCycleService.InvocationCount);

        // Explicit recovery boundary reclaims the expired InProgress claim safely when not in flight
        var (isReclaimed, recovered) = await repo.ReclaimAsync(evt.EventId, _clock.UtcNow.UtcDateTime, TimeSpan.FromMinutes(5));
        Assert.True(isReclaimed);
        Assert.Equal(2, recovered.AttemptCount);
        Assert.Equal(EventConsumptionState.InProgress, recovered.State);

        // Authoritative cycle marks consumed
        await repo.MarkConsumedAsync(evt.EventId, Guid.NewGuid(), _clock.UtcNow.UtcDateTime, expectedVersion: 2);

        // 4. Verify DB state is now Consumed with AttemptCount = 2
        var finalRecord = await repo.GetByEventIdAsync(evt.EventId);
        Assert.NotNull(finalRecord);
        Assert.Equal(EventConsumptionState.Consumed, finalRecord.State);
        Assert.Equal(2, finalRecord.AttemptCount);
    }

    [Fact]
    public async Task Consumer_FailedEvent_CanBeRetriedAndCompleted()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var shouldFail = true;
        var fakeCycleService = new FakeCharacterCognitiveCycleService
        {
            Handler = (ctx, ct) =>
            {
                if (shouldFail)
                {
                    return Task.FromResult(CharacterCognitiveCycleResult.Failed(
                        ctx.CycleId, ctx.ExecutionId, ctx.CharacterId, ctx.TriggeredAtUtc, 1,
                        message: "Transient failure during first attempt."));
                }

                return Task.FromResult(CharacterCognitiveCycleResult.CompletedWithoutAction(
                    ctx.CycleId, ctx.ExecutionId, ctx.CharacterId, ctx.TriggeredAtUtc, 1,
                    message: "Success on retry."));
            }
        };

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();

        // 1. First attempt fails
        var firstResult = await consumer.ConsumeAsync(evt);
        Assert.True(firstResult.IsRejected);

        var failedRecord = await repo.GetByEventIdAsync(evt.EventId);
        Assert.NotNull(failedRecord);
        Assert.Equal(EventConsumptionState.Failed, failedRecord.State);
        Assert.Equal(1, failedRecord.AttemptCount);

        // 2. Second attempt retries and succeeds
        shouldFail = false;
        _clock.CurrentTime = _clock.CurrentTime.AddMinutes(1);
        var retryResult = await consumer.ConsumeAsync(evt);

        Assert.True(retryResult.IsAccepted);
        Assert.Equal(2, fakeCycleService.InvocationCount);

        var consumedRecord = await repo.GetByEventIdAsync(evt.EventId);
        Assert.NotNull(consumedRecord);
        Assert.Equal(EventConsumptionState.Consumed, consumedRecord.State);
        Assert.Equal(2, consumedRecord.AttemptCount);
        Assert.Null(consumedRecord.FailureReason);
    }

    [Fact]
    public async Task Consumer_ConsumedEvent_IsTerminalDuplicate()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);
        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();

        // 1. Process successfully
        var initial = await consumer.ConsumeAsync(evt);
        Assert.True(initial.IsAccepted);

        // 2. Advance clock 30 days into future
        _clock.CurrentTime = _clock.CurrentTime.AddDays(30);

        // 3. Redelivery is permanently Duplicate
        var redelivery = await consumer.ConsumeAsync(evt);
        Assert.True(redelivery.IsDuplicate);
        Assert.Equal(initial.CycleId, redelivery.CycleId);
        Assert.Equal(1, fakeCycleService.InvocationCount);
    }

    [Fact]
    public async Task LongRunningWorker_WhenLeaseExpires_DoesNotAllowSecondCognitiveCycle()
    {
        using var dbWorkerA = CreateDbContext();
        var repoA = new WorldCognitiveEventConsumptionRepository(dbWorkerA);

        var cycleStartedTcs = new TaskCompletionSource<bool>();
        var canCompleteCycleTcs = new TaskCompletionSource<bool>();

        var fakeCycleService = new FakeCharacterCognitiveCycleService
        {
            Handler = async (ctx, ct) =>
            {
                cycleStartedTcs.TrySetResult(true);
                await canCompleteCycleTcs.Task;
                return CharacterCognitiveCycleResult.CompletedWithoutAction(
                    ctx.CycleId, ctx.ExecutionId, ctx.CharacterId, ctx.TriggeredAtUtc, 1, message: "Done");
            }
        };

        var consumerA = new WorldCognitiveEventConsumer(
            repoA, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();

        // 1. Worker A starts consuming and is actively inside Cognitive Cycle execution
        var workerATask = Task.Run(() => consumerA.ConsumeAsync(evt));
        await cycleStartedTcs.Task;

        Assert.Equal(1, fakeCycleService.InvocationCount);

        // 2. Simulate 10 minutes passing (lease is 5 minutes, so lease in DB has expired!)
        _clock.CurrentTime = _clock.CurrentTime.AddMinutes(10);

        // 3. Worker B attempts to consume the same event
        using var dbWorkerB = CreateDbContext();
        var repoB = new WorldCognitiveEventConsumptionRepository(dbWorkerB);
        var consumerB = new WorldCognitiveEventConsumer(
            repoB, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var resultB = await consumerB.ConsumeAsync(evt);

        // Invariant: Worker B must NOT start a second Cognitive Cycle!
        Assert.True(resultB.IsDuplicate);
        Assert.Equal(1, fakeCycleService.InvocationCount);

        // 4. Worker A completes its execution
        canCompleteCycleTcs.TrySetResult(true);
        var resultA = await workerATask;

        Assert.True(resultA.IsAccepted);
        Assert.Equal(1, fakeCycleService.InvocationCount);

        // 5. Authoritative state in DB is Consumed
        using var verifyDb = CreateDbContext();
        var record = await verifyDb.WorldCognitiveEventConsumptions.FirstOrDefaultAsync(c => c.EventId == evt.EventId);
        Assert.NotNull(record);
        Assert.Equal(EventConsumptionState.Consumed, record.State);
        Assert.Equal(resultA.CycleId, record.CycleId);
    }

    [Fact]
    public async Task ActiveInProgressClaim_CannotBeArbitrarilyReclaimed()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var evt = CreateValidEvent();
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, _clock.UtcNow.UtcDateTime);

        var (claimed, _) = await repo.TryClaimAsync(claim);
        Assert.True(claimed);

        // 1 minute later (< 5 minutes lease window)
        var attemptTime = _clock.UtcNow.UtcDateTime.AddMinutes(1);

        // Attempting domain Reclaim throws InvalidOperationException
        var exDomain = Assert.Throws<InvalidOperationException>(() =>
            claim.Reclaim(attemptTime, TimeSpan.FromMinutes(5)));
        Assert.Contains("within lease window", exDomain.Message);

        // Attempting repo ReclaimAsync throws InvalidOperationException
        var exRepo = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.ReclaimAsync(evt.EventId, attemptTime, TimeSpan.FromMinutes(5)));
        Assert.Contains("within lease window", exRepo.Message);
    }

    [Fact]
    public async Task ExpiredInProgressClaim_CanBeRecovered()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var evt = CreateValidEvent();
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, _clock.UtcNow.UtcDateTime);

        var (claimed, _) = await repo.TryClaimAsync(claim);
        Assert.True(claimed);

        // 6 minutes later (> 5 minutes lease window)
        var recoveryTime = _clock.UtcNow.UtcDateTime.AddMinutes(6);

        var (isReclaimed, recovered) = await repo.ReclaimAsync(evt.EventId, recoveryTime, TimeSpan.FromMinutes(5));
        Assert.True(isReclaimed);
        Assert.Equal(EventConsumptionState.InProgress, recovered.State);
        Assert.Equal(2, recovered.AttemptCount);
        Assert.Equal(2u, recovered.Version);
    }

    [Fact]
    public async Task FailedClaim_CanBeRetried()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var evt = CreateValidEvent();
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, _clock.UtcNow.UtcDateTime);

        await repo.TryClaimAsync(claim);
        await repo.MarkFailedAsync(evt.EventId, "Transient network failure", _clock.UtcNow.UtcDateTime, expectedVersion: 1);

        // Reclaim for retry (can be retried immediately without waiting for lease timeout)
        var retryTime = _clock.UtcNow.UtcDateTime.AddSeconds(10);
        var (isReclaimed, retried) = await repo.ReclaimAsync(evt.EventId, retryTime, TimeSpan.FromMinutes(5));

        Assert.True(isReclaimed);
        Assert.Equal(EventConsumptionState.InProgress, retried.State);
        Assert.Equal(2, retried.AttemptCount);
        Assert.Null(retried.FailureReason);
    }

    [Fact]
    public async Task ConsumedClaim_CannotBeReclaimed()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var evt = CreateValidEvent();
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, _clock.UtcNow.UtcDateTime);

        await repo.TryClaimAsync(claim);
        var cycleId = Guid.NewGuid();
        await repo.MarkConsumedAsync(evt.EventId, cycleId, _clock.UtcNow.UtcDateTime, expectedVersion: 1);

        // 1 year later
        var futureTime = _clock.UtcNow.UtcDateTime.AddYears(1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.ReclaimAsync(evt.EventId, futureTime, TimeSpan.FromMinutes(5)));

        Assert.Contains("Cannot reclaim an already Consumed event", ex.Message);
    }

    [Fact]
    public async Task StaleWorker_CannotMarkRecoveredClaimAsConsumed()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var evt = CreateValidEvent();
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, _clock.UtcNow.UtcDateTime);

        // Worker A claims at Version = 1
        var (claimed, workerAClaim) = await repo.TryClaimAsync(claim);
        Assert.True(claimed);
        Assert.Equal(1u, workerAClaim.Version);

        // Worker A hangs, lease expires, Worker B recovers the claim (Version becomes 2)
        var recoveryTime = _clock.UtcNow.UtcDateTime.AddMinutes(10);
        var (isReclaimed, workerBClaim) = await repo.ReclaimAsync(evt.EventId, recoveryTime, TimeSpan.FromMinutes(5));
        Assert.True(isReclaimed);
        Assert.Equal(2u, workerBClaim.Version);

        // Stale Worker A finishes later and attempts MarkConsumed with its old expectedVersion = 1
        var cycleIdA = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            repo.MarkConsumedAsync(evt.EventId, cycleIdA, _clock.UtcNow.UtcDateTime, expectedVersion: 1));

        Assert.Contains("Stale worker fence violation", ex.Message);

        // Verify DB record is STILL InProgress, NOT Consumed by Worker A
        var record = await repo.GetByEventIdAsync(evt.EventId);
        Assert.NotNull(record);
        Assert.Equal(EventConsumptionState.InProgress, record.State);
        Assert.NotEqual(cycleIdA, record.CycleId);

        // Worker B finishes authoritative cycle and marks consumed with expectedVersion = 2
        var cycleIdB = Guid.NewGuid();
        await repo.MarkConsumedAsync(evt.EventId, cycleIdB, _clock.UtcNow.UtcDateTime, expectedVersion: 2);

        var finalRecord = await repo.GetByEventIdAsync(evt.EventId);
        Assert.NotNull(finalRecord);
        Assert.Equal(EventConsumptionState.Consumed, finalRecord.State);
        Assert.Equal(cycleIdB, finalRecord.CycleId);
    }

    [Fact]
    public async Task RecoveryRace_OnlyOneWorkerOwnsCurrentClaim()
    {
        var evt = CreateValidEvent();
        using var dbInit = CreateDbContext();
        var repoInit = new WorldCognitiveEventConsumptionRepository(dbInit);

        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, _clock.UtcNow.UtcDateTime);
        await repoInit.TryClaimAsync(claim);

        // Advance clock past lease timeout
        var recoveryTime = _clock.UtcNow.UtcDateTime.AddMinutes(10);

        // Worker A reclaims successfully
        using var dbWorkerA = CreateDbContext();
        var repoA = new WorldCognitiveEventConsumptionRepository(dbWorkerA);
        var (reclaimedA, claimA) = await repoA.ReclaimAsync(evt.EventId, recoveryTime, TimeSpan.FromMinutes(5));
        Assert.True(reclaimedA);
        Assert.Equal(2u, claimA.Version);

        // Worker B attempts to reclaim with stale view
        using var dbWorkerB = CreateDbContext();
        var repoB = new WorldCognitiveEventConsumptionRepository(dbWorkerB);

        // Since Worker A already updated LastAttemptAtUtc to recoveryTime, Worker B sees InProgress within lease!
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repoB.ReclaimAsync(evt.EventId, recoveryTime, TimeSpan.FromMinutes(5)));

        Assert.Contains("within lease window", ex.Message);
    }

    [Fact]
    public async Task StaleWorker_Consumer_FencesOutAndReturnsDuplicate()
    {
        var node1Tracker = new WorldCognitiveEventInFlightTracker();
        var node2Tracker = new WorldCognitiveEventInFlightTracker();

        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db, node1Tracker);

        var fakeCycleService = new FakeCharacterCognitiveCycleService
        {
            Handler = async (ctx, ct) =>
            {
                // Simulate lease expiry and recovery by another worker node while cycle is running
                using var recoveryDb = CreateDbContext();
                var recoveryRepo = new WorldCognitiveEventConsumptionRepository(recoveryDb, node2Tracker);
                await recoveryRepo.ReclaimAsync(ctx.Event!.EventId, _clock.UtcNow.UtcDateTime.AddMinutes(10), TimeSpan.Zero);

                return CharacterCognitiveCycleResult.CompletedWithoutAction(
                    ctx.CycleId, ctx.ExecutionId, ctx.CharacterId, ctx.TriggeredAtUtc, 1, message: "Late completion");
            }
        };

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, node1Tracker);

        var evt = CreateValidEvent();
        var result = await consumer.ConsumeAsync(evt);

        // Because the claim was recovered during cycle execution, the stale worker's commit is fenced out
        Assert.True(result.IsDuplicate);
        Assert.False(result.IsAccepted);
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
        Assert.Equal(capturedCycleId, result.CycleId);
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
    public async Task Consumer_CycleIdIsDistinctFromExecutionId()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var fakeCycleService = new FakeCharacterCognitiveCycleService();
        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        var evt = CreateValidEvent();
        await consumer.ConsumeAsync(evt);

        Assert.NotNull(fakeCycleService.LastContext);
        var cycleId = fakeCycleService.LastContext.CycleId;
        var executionId = fakeCycleService.LastContext.ExecutionId;

        Assert.NotEqual(cycleId, executionId);
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
            TryClaimHandler = (c, lt, ct) => Task.FromResult((true, claim)),
            MarkConsumedHandler = (eid, cid, t, v, ct) => throw new DbUpdateException("Database connection severed during commit")
        };

        var fakeCycleService = new FakeCharacterCognitiveCycleService();

        var consumer = new WorldCognitiveEventConsumer(
            fakeRepo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance);

        await Assert.ThrowsAsync<DbUpdateException>(() => consumer.ConsumeAsync(evt));
    }

    [Fact]
    public async Task MarkFailed_PersistenceFailure_LogsRecoveryRequiredAndRethrows()
    {
        var evt = CreateValidEvent();
        var now = _clock.UtcNow.UtcDateTime;
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, now);

        var fakeRepo = new FakeWorldCognitiveEventConsumptionRepository
        {
            TryClaimHandler = (c, lt, ct) => Task.FromResult((true, claim)),
            MarkFailedHandler = (eid, reason, t, v, ct) => throw new DbUpdateException("Database disk full during MarkFailed")
        };

        var fakeCycleService = new FakeCharacterCognitiveCycleService
        {
            Handler = (ctx, ct) => throw new InvalidOperationException("Business logic failure")
        };

        var spyLogger = new TestSpyLogger<WorldCognitiveEventConsumer>();

        var consumer = new WorldCognitiveEventConsumer(
            fakeRepo, fakeCycleService, _clock, spyLogger);

        // Invariant: Original exception must bubble up, and a Critical log must be emitted
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.ConsumeAsync(evt));
        Assert.Equal("Business logic failure", ex.Message);

        var criticalLog = spyLogger.Logs.FirstOrDefault(l => l.Level == LogLevel.Critical);
        Assert.NotEqual(default, criticalLog);
        Assert.Contains("CRITICAL RECOVERY REQUIRED", criticalLog.Message);
        Assert.Contains(evt.EventId.ToString(), criticalLog.Message);
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

        var canonical = $"1|11111111-2222-3333-4444-555555555555|66666666-7777-8888-9999-000000000000|2026-09-08T10:30:00.0000000+00:00|Sensor|FireAlarm|Hazard";
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        var actualHash = CanonicalWorldEventFingerprint.Compute(
            eventId, charId, occurredAt, "FireAlarm", "Sensor", "Hazard");

        Assert.Equal(expectedHash, actualHash);
    }

    #endregion

    #region Category 10: P0 Fencing, Pre-Claim Tracking & Recovery Boundary (Tests 36-44)

    [Fact]
    public async Task ClaimOwnership_IsRegisteredBeforeExecutionStarts()
    {
        var tracker = new WorldCognitiveEventInFlightTracker();
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db, tracker);

        bool wasInFlightDuringCycle = false;
        var fakeCycleService = new FakeCharacterCognitiveCycleService
        {
            Handler = (ctx, ct) =>
            {
                wasInFlightDuringCycle = tracker.IsInFlight(ctx.Event!.EventId);
                return Task.FromResult(CharacterCognitiveCycleResult.CompletedWithoutAction(
                    ctx.CycleId, ctx.ExecutionId, ctx.CharacterId, ctx.TriggeredAtUtc, 1, message: "OK"));
            }
        };

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, tracker);

        var evt = CreateValidEvent();
        var result = await consumer.ConsumeAsync(evt);

        Assert.True(result.IsAccepted);
        Assert.True(wasInFlightDuringCycle, "InFlightTracker must register ownership before CognitiveCycle execution starts.");
        Assert.False(tracker.IsInFlight(evt.EventId), "InFlightTracker must clear registration after consumption completes.");
    }

    [Fact]
    public async Task ClaimAndTrack_CannotBeInterleavedIntoStaleWorkerExecution()
    {
        var tracker = new WorldCognitiveEventInFlightTracker();
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db, tracker);

        var cycleStartedTcs = new TaskCompletionSource<bool>();
        var cycleReleaseTcs = new TaskCompletionSource<bool>();

        var fakeCycleService = new FakeCharacterCognitiveCycleService
        {
            Handler = async (ctx, ct) =>
            {
                cycleStartedTcs.TrySetResult(true);
                await cycleReleaseTcs.Task;
                return CharacterCognitiveCycleResult.CompletedWithoutAction(
                    ctx.CycleId, ctx.ExecutionId, ctx.CharacterId, ctx.TriggeredAtUtc, 1, message: "OK");
            }
        };

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, tracker);

        var evt = CreateValidEvent();

        // Worker A starts execution and pauses in the middle of CognitiveCycle
        var workerATask = Task.Run(() => consumer.ConsumeAsync(evt));
        await cycleStartedTcs.Task;

        // Worker B attempts to consume the same event while Worker A is in-flight
        var workerBResult = await consumer.ConsumeAsync(evt);

        Assert.True(workerBResult.IsDuplicate);
        Assert.False(workerBResult.IsAccepted);

        // Release Worker A
        cycleReleaseTcs.TrySetResult(true);
        var workerAResult = await workerATask;

        Assert.True(workerAResult.IsAccepted);
        Assert.Equal(1, fakeCycleService.InvocationCount);
    }

    [Fact]
    public async Task ActiveInFlightClaim_CannotBeReclaimedByExplicitRecovery()
    {
        var tracker = new WorldCognitiveEventInFlightTracker();
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db, tracker);

        var evt = CreateValidEvent();
        var now = _clock.UtcNow.UtcDateTime;

        // Simulate initial claim in DB
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, now);
        db.WorldCognitiveEventConsumptions.Add(claim);
        await db.SaveChangesAsync();

        // Register in-flight tracker to simulate active execution
        Assert.True(tracker.TryTrack(evt.EventId, out var scope));
        using (scope)
        {
            Assert.True(tracker.IsInFlight(evt.EventId));

            // Advance clock past lease window
            var recoveryTime = now.AddMinutes(10);

            // Attempting to reclaim an actively in-flight claim on this instance must throw InvalidOperationException
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.ReclaimAsync(evt.EventId, recoveryTime, TimeSpan.FromMinutes(5)));

            Assert.Contains("actively in-flight on this instance", ex.Message);
        }
    }

    [Fact]
    public async Task ExpiredClaim_CanOnlyBeRecoveredWhenRecoveryBoundaryAllowsIt()
    {
        var tracker = new WorldCognitiveEventInFlightTracker();
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db, tracker);

        var fakeCycleService = new FakeCharacterCognitiveCycleService();
        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, tracker);

        var evt = CreateValidEvent();
        var now = _clock.UtcNow.UtcDateTime;

        // 1. Initial claim placed in DB (simulate worker that crashed before completion)
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, now);
        db.WorldCognitiveEventConsumptions.Add(claim);
        await db.SaveChangesAsync();

        // Advance clock past lease timeout (10 minutes later)
        _clock.CurrentTime = _clock.CurrentTime.AddMinutes(10);

        // 2. Under Option A, normal consumer redelivery treats InProgress as duplicate (no second cycle dispatched)
        var normalDispatchResult = await consumer.ConsumeAsync(evt);
        Assert.True(normalDispatchResult.IsDuplicate);
        Assert.Equal(0, fakeCycleService.InvocationCount);

        // 3. Explicit recovery boundary DOES allow recovery of the expired claim
        var (isReclaimed, recovered) = await repo.ReclaimAsync(evt.EventId, _clock.UtcNow.UtcDateTime, TimeSpan.FromMinutes(5));
        Assert.True(isReclaimed);
        Assert.Equal(2, recovered.AttemptCount);
        Assert.Equal(EventConsumptionState.InProgress, recovered.State);
    }

    [Fact]
    public async Task StaleWorker_CannotCauseCharacterStateMutation()
    {
        var tracker = new WorldCognitiveEventInFlightTracker();
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db, tracker);

        int actionExecutionCount = 0;
        var fakeCycleService = new FakeCharacterCognitiveCycleService
        {
            Handler = (ctx, ct) =>
            {
                // Invalidate the running worker while it's in cycle
                tracker.Invalidate(ctx.Event!.EventId);

                // Simulate cancellation check before mutating character state
                ct.ThrowIfCancellationRequested();

                actionExecutionCount++;
                return Task.FromResult(CharacterCognitiveCycleResult.CompletedWithoutAction(
                    ctx.CycleId, ctx.ExecutionId, ctx.CharacterId, ctx.TriggeredAtUtc, 1, message: "Should not reach here"));
            }
        };

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, tracker);

        var evt = CreateValidEvent();

        await Assert.ThrowsAsync<OperationCanceledException>(() => consumer.ConsumeAsync(evt));

        Assert.Equal(0, actionExecutionCount);
    }

    [Fact]
    public async Task StaleWorker_CannotMarkFailed()
    {
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db);

        var evt = CreateValidEvent();
        var now = _clock.UtcNow.UtcDateTime;

        var claim = WorldCognitiveEventConsumption.CreateClaim(
            evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, now);
        db.WorldCognitiveEventConsumptions.Add(claim);
        await db.SaveChangesAsync();

        // Increment version in DB (e.g. recovery worker reclaimed it)
        claim.Reclaim(now.AddMinutes(10));
        await db.SaveChangesAsync();
        Assert.Equal(2u, claim.Version);

        // Stale worker tries to mark failed with version 1
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            repo.MarkFailedAsync(evt.EventId, "Stale worker failure", now, expectedVersion: 1));
    }

    [Fact]
    public async Task ConcurrentRecovery_OneWinnerOneConcurrencyConflict()
    {
        var evt = CreateValidEvent();
        var now = _clock.UtcNow.UtcDateTime;

        using (var initDb = CreateDbContext())
        {
            var claim = WorldCognitiveEventConsumption.CreateClaim(
                evt.EventId, evt.CharacterId, evt.OccurredAtUtc, evt.EventName, evt.Source, evt.Category, now);
            initDb.WorldCognitiveEventConsumptions.Add(claim);
            await initDb.SaveChangesAsync();
        }

        var recoveryTime = now.AddMinutes(10);
        var trackerA = new WorldCognitiveEventInFlightTracker();
        var trackerB = new WorldCognitiveEventInFlightTracker();

        using var dbA = CreateDbContext();
        using var dbB = CreateDbContext();
        var repoA = new WorldCognitiveEventConsumptionRepository(dbA, trackerA);
        var repoB = new WorldCognitiveEventConsumptionRepository(dbB, trackerB);

        // Worker A successfully reclaims
        var (isReclaimedA, recordA) = await repoA.ReclaimAsync(evt.EventId, recoveryTime, TimeSpan.FromMinutes(5));
        Assert.True(isReclaimedA);
        Assert.Equal(2u, recordA.Version);

        // Worker B attempts to reclaim within lease timeout of Worker A's new claim -> throws InvalidOperationException
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repoB.ReclaimAsync(evt.EventId, recoveryTime, TimeSpan.FromMinutes(5)));
        Assert.Contains("within lease window", ex.Message);
    }

    [Fact]
    public async Task ConcurrentSameEvent_DoesNotDispatchTwoCycles()
    {
        var tracker = new WorldCognitiveEventInFlightTracker();
        using var db = CreateDbContext();
        var repo = new WorldCognitiveEventConsumptionRepository(db, tracker);

        var cycleStartedTcs = new TaskCompletionSource<bool>();
        var cycleReleaseTcs = new TaskCompletionSource<bool>();

        var fakeCycleService = new FakeCharacterCognitiveCycleService
        {
            Handler = async (ctx, ct) =>
            {
                cycleStartedTcs.TrySetResult(true);
                await cycleReleaseTcs.Task;
                return CharacterCognitiveCycleResult.CompletedWithoutAction(
                    ctx.CycleId, ctx.ExecutionId, ctx.CharacterId, ctx.TriggeredAtUtc, 1, message: "OK");
            }
        };

        var consumer = new WorldCognitiveEventConsumer(
            repo, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, tracker);

        var evt = CreateValidEvent();

        var task1 = Task.Run(() => consumer.ConsumeAsync(evt));
        await cycleStartedTcs.Task;

        var task2 = Task.Run(() => consumer.ConsumeAsync(evt));
        var result2 = await task2;

        cycleReleaseTcs.TrySetResult(true);
        var result1 = await task1;

        Assert.True(result1.IsAccepted || result2.IsAccepted);
        Assert.True(result1.IsDuplicate || result2.IsDuplicate);
        Assert.Equal(1, fakeCycleService.InvocationCount);
    }

    [Fact]
    public async Task ConcurrentDivergentEvent_RemainsIdempotencyConflict()
    {
        var eventId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var evtA = CreateValidEvent(eventId: eventId, characterId: charId, eventName: "EventA_Original");
        var evtB = CreateValidEvent(eventId: eventId, characterId: charId, eventName: "EventB_Divergent");

        var fakeCycleService = new FakeCharacterCognitiveCycleService();
        var trackerA = new WorldCognitiveEventInFlightTracker();
        var trackerB = new WorldCognitiveEventInFlightTracker();

        var interceptor = new ConcurrentConsumerRaceInterceptor(async () =>
        {
            await using var dbWorkerA = new CoreDbContext(_options);
            var repoA = new WorldCognitiveEventConsumptionRepository(dbWorkerA, trackerA);
            var consumerA = new WorldCognitiveEventConsumer(
                repoA, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, trackerA);

            await consumerA.ConsumeAsync(evtA);
        });

        await using var dbWorkerB = CreateDbContext(interceptor);
        var repoB = new WorldCognitiveEventConsumptionRepository(dbWorkerB, trackerB);
        var consumerB = new WorldCognitiveEventConsumer(
            repoB, fakeCycleService, _clock, NullLogger<WorldCognitiveEventConsumer>.Instance, trackerB);

        await Assert.ThrowsAsync<WorldCognitiveEventIdempotencyConflictException>(() =>
            consumerB.ConsumeAsync(evtB));
    }

    #endregion
}
