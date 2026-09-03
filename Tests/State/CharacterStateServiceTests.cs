using Application.Common;
using Application.Enums;
using Domain.Entities;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.State;

public sealed class CharacterStateServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterStateServiceTests()
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
    public async Task GetOrCreateInitialStateAsync_CreatesAndPersistsDefaultState()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = new CoreDbContext(_options);
        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

        var snapshot = await stateService.GetOrCreateInitialStateAsync(charId, now);

        Assert.NotNull(snapshot);
        Assert.Equal(80, snapshot.Energy);
        Assert.Equal(20, snapshot.Hunger);
        Assert.Equal(1, snapshot.Version);

        // Verify persisted in DB
        var persisted = await db.CharacterStates.FirstOrDefaultAsync(s => s.CharacterId == charId);
        Assert.NotNull(persisted);
        Assert.Equal(charId, persisted.CharacterId);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task ApplyDeltaAsync_UpdatesStateAndRecordsLedgerTransition()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = new CoreDbContext(_options);
        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

        await stateService.GetOrCreateInitialStateAsync(charId, now);

        var execId = Guid.NewGuid();
        var delta = new CharacterStateDelta(hungerDelta: 15m, energyDelta: -20m);
        var context = new StateTransitionContext(execId, "Activity", "act-1", "Test delta");

        var result = await stateService.ApplyDeltaAsync(charId, delta, context, now);

        Assert.Equal(StateTransitionResultStatus.Applied, result.Status);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(1, result.VersionBefore);
        Assert.Equal(2, result.VersionAfter);
        Assert.Equal(35, result.Snapshot.Hunger); // 20 + 15
        Assert.Equal(60, result.Snapshot.Energy); // 80 - 20

        // Verify ledger transition
        var transition = await db.CharacterStateTransitions.FirstOrDefaultAsync(t => t.CharacterId == charId && t.ExecutionId == execId);
        Assert.NotNull(transition);
        Assert.Equal(15m, transition.HungerDelta);
        Assert.Equal(-20m, transition.EnergyDelta);
        Assert.Equal(1, transition.VersionBefore);
        Assert.Equal(2, transition.VersionAfter);
    }

    [Fact]
    public async Task ApplyDeltaAsync_WithDuplicateExecutionId_ReturnsAlreadyAppliedAndDoesNotDoubleApply()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = new CoreDbContext(_options);
        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

        await stateService.GetOrCreateInitialStateAsync(charId, now);

        var execId = Guid.NewGuid();
        var delta = new CharacterStateDelta(hungerDelta: 10m);
        var context = new StateTransitionContext(execId, "Reaction", "event-1");

        // First application
        var result1 = await stateService.ApplyDeltaAsync(charId, delta, context, now);
        Assert.Equal(StateTransitionResultStatus.Applied, result1.Status);
        Assert.Equal(2, result1.VersionAfter);

        // Duplicate application with same ExecutionId
        var result2 = await stateService.ApplyDeltaAsync(charId, delta, context, now);
        Assert.Equal(StateTransitionResultStatus.AlreadyApplied, result2.Status);
        Assert.Equal(2, result2.VersionAfter);

        // Ensure state in DB was only updated once
        var state = await db.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.Equal(30m, state.Hunger); // 20 + 10, NOT 40
        Assert.Equal(2, state.Version);

        // Ensure only one transition record exists
        var count = await db.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId && t.ExecutionId == execId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task EvolveToAsync_AdvancesTimeAndAppliesEvolutionDelta()
    {
        var charId = Guid.NewGuid();
        var t0 = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddHours(2);

        using var db = new CoreDbContext(_options);
        var transitionService = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);
        var stateService = new CharacterStateService(db, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

        await stateService.GetOrCreateInitialStateAsync(charId, t0);

        var result = await stateService.EvolveToAsync(charId, t1);

        Assert.Equal(StateTransitionResultStatus.Applied, result.Status);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(2, result.VersionAfter);
        Assert.Equal(28, result.Snapshot.Hunger); // 20 + (2 * 4)
        Assert.Equal(70, result.Snapshot.Energy); // 80 - (2 * 5)
        Assert.Equal(t1, result.Snapshot.LastEvolvedAtUtc);
    }
}
