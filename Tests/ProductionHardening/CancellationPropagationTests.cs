using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.LifeSimulation;
using Application.Contracts.Safety;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Health;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.CognitiveCycle;
using Infrastructure.Services.LifeSimulation;
using Infrastructure.Services.Safety;
using Infrastructure.Services.State;
using Infrastructure.Services.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Infrastructure.Services.Autonomous;
using Infrastructure.Services.SocialPresence;
using Tests.LifeSimulation;
using Xunit;

namespace Project.Tests.ProductionHardening;

public class CancellationPropagationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CancellationPropagationTests()
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

    private CharacterCognitiveCycleService CreateCycleService(
        CoreDbContext db,
        IActionSafetyGate? safetyGate = null,
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

        var gate = safetyGate ?? new ActionSafetyGate(
            stateService,
            Array.Empty<IActionSafetyPolicy>(),
            NullLogger<ActionSafetyGate>.Instance);

        return new CharacterCognitiveCycleService(
            stateService: stateService,
            experiencePolicy: new CharacterInternalExperiencePolicy(),
            appraisalPolicy: new CharacterAppraisalPolicy(),
            emotionPolicy: new CharacterEmotionPolicy(),
            desirePolicy: new CharacterDesirePolicy(),
            intentPolicy: new CharacterIntentPolicy(),
            actionProposalPolicy: new CharacterActionProposalPolicy(),
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance,
            safetyGate: gate);
    }

    [Fact]
    public async Task CognitiveCycle_Cancellation_Propagates()
    {
        await using var db = new CoreDbContext(_options);
        var service = CreateCycleService(db);

        var charId = Guid.NewGuid();
        var state = new CharacterState(charId, DateTime.UtcNow, hunger: 90m, energy: 70m, stress: 10m);
        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cycleContext = new CharacterCognitiveCycleContext(Guid.NewGuid(), Guid.NewGuid(), charId, DateTime.UtcNow);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(cycleContext, cts.Token));
    }

    [Fact]
    public async Task SafetyGate_Cancellation_Propagates()
    {
        await using var db = new CoreDbContext(_options);
        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

        var safetyGate = new ActionSafetyGate(
            stateService,
            Array.Empty<IActionSafetyPolicy>(),
            NullLogger<ActionSafetyGate>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var proposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.8,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            safetyGate.EvaluateAsync(Guid.NewGuid(), proposal, cts.Token));
    }

    [Fact]
    public async Task LifeSimulation_Cancellation_Propagates()
    {
        await using var db = new CoreDbContext(_options);
        db.EnsureLifeSimulationTriggersCreated();

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

        var charId = Guid.NewGuid();
        var character = new Character(
            "Test", "Title", "https://example.com/avatar.jpg",
            "Prompt", "Hi", "Category") { Id = charId };
        db.Characters.Add(character);
        var state = new CharacterState(charId, DateTime.UtcNow);
        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var simContext = new CharacterLifeSimulationContext(charId, simClock.UtcNow, Guid.NewGuid());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TickAsync(simContext, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ScheduleActivityAsync(charId, LifeActivityType.Rest, simClock.UtcNow, simClock.UtcNow.AddMinutes(30), ct: cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CancelActivityAsync(Guid.NewGuid(), simClock.UtcNow, "Cancel test", cts.Token));
    }

    [Fact]
    public async Task Cancellation_DoesNotCreateFakeBusinessFailureResult()
    {
        await using var db = new CoreDbContext(_options);
        var service = CreateCycleService(db);

        var charId = Guid.NewGuid();
        var state = new CharacterState(charId, DateTime.UtcNow, hunger: 90m, energy: 70m, stress: 10m);
        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cycleContext = new CharacterCognitiveCycleContext(Guid.NewGuid(), Guid.NewGuid(), charId, DateTime.UtcNow);

        // Must throw OperationCanceledException and NOT swallow to return a completed cycle result
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(cycleContext, cts.Token));

        // State transitions table must remain empty
        var transitions = await db.CharacterStateTransitions.ToListAsync();
        Assert.Empty(transitions);
    }

    [Fact]
    public async Task CoreDbContextHealthCheck_PropagatesCancellation_WhenCancellationTokenTriggered()
    {
        await using var db = new CoreDbContext(_options);
        var healthCheck = new CoreDbContextHealthCheck(db);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            healthCheck.CheckHealthAsync(context, cts.Token));
    }

    [Fact]
    public async Task AutonomousCharacterService_Cancellation_Propagates()
    {
        await using var db = new CoreDbContext(_options);
        var cycleService = CreateCycleService(db);
        var tickRepo = new CharacterAutonomousLifeTickRepository(db);
        var autoService = new AutonomousCharacterService(
            cycleService,
            tickRepo,
            NullLogger<AutonomousCharacterService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            autoService.RunOnceAsync(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, cts.Token));
    }

    [Fact]
    public async Task WorldCognitiveEventConsumer_Cancellation_Propagates()
    {
        await using var db = new CoreDbContext(_options);
        var cycleService = CreateCycleService(db);
        var consumptionRepo = new WorldCognitiveEventConsumptionRepository(db);
        var consumer = new WorldCognitiveEventConsumer(
            consumptionRepo,
            cycleService,
            new SystemClock(),
            NullLogger<WorldCognitiveEventConsumer>.Instance);

        var worldEvt = new WorldCognitiveEvent(
            EventId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Source: "WorldTest",
            EventName: "WeatherChanged");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            consumer.ConsumeAsync(worldEvt, cts.Token));
    }

    [Fact]
    public async Task SocialPresenceTransitionService_Cancellation_Propagates()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterSocialPresenceRepository(db);
        var service = new SocialPresenceTransitionService(
            repo,
            NullLogger<SocialPresenceTransitionService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, cts.Token));
    }
}
