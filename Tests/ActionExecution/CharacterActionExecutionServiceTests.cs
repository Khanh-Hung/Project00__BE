using System;
using System.Linq;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.ActionExecution;

public sealed class CharacterActionExecutionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterActionExecutionServiceTests()
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
        decimal comfort = 50m)
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
        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();
        return charId;
    }

    private CharacterActionExecutionService CreateService(CoreDbContext db)
    {
        var transitionService = new CharacterStateTransitionService(
            db,
            NullLogger<CharacterStateTransitionService>.Instance);

        var policy = new CharacterActionExecutionPolicy();

        return new CharacterActionExecutionService(
            transitionService,
            policy,
            NullLogger<CharacterActionExecutionService>.Instance);
    }

    #region 1. Happy Path & Provenance Preservation

    [Fact]
    public async Task ExecuteAsync_AppliesStateTransition_AndPreservesFullProvenance()
    {
        var charId = await SeedCharacterStateAsync(hunger: 80m);
        var executionId = Guid.NewGuid();

        var proposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.8,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        var context = new CharacterActionExecutionContext(
            ExecutionId: executionId,
            ExecutedAtUtc: new DateTimeOffset(2026, 9, 4, 10, 30, 0, TimeSpan.Zero)
        );

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.ExecuteAsync(charId, proposal, context);

        Assert.NotNull(result);
        Assert.Equal(CharacterActionExecutionStatus.Applied, result.Status);
        Assert.True(result.IsSuccess);
        Assert.True(result.IsApplied);
        Assert.False(result.IsDuplicateSuppressed);

        // Provenance verification
        Assert.Equal(executionId, result.ExecutionId);
        Assert.Equal(charId, result.CharacterId);
        Assert.Equal(ActionType.Eat, result.ActionType);
        Assert.Equal(0.8, result.Intensity);
        Assert.Equal(IntentType.SeekFood, result.SourceIntent);
        Assert.Equal(MotivationType.HungerDriven, result.Motivation);

        // Version & Delta verification
        Assert.Equal(1, result.StateVersionBefore);
        Assert.Equal(2, result.StateVersionAfter);
        Assert.NotNull(result.AppliedDelta);
        Assert.Equal(-24.0m, result.AppliedDelta.HungerDelta); // -30 * 0.8 = -24.0

        // Snapshot verification
        Assert.NotNull(result.Snapshot);
        Assert.Equal(56m, result.Snapshot.Hunger); // 80 - 24 = 56
        Assert.Equal(2, result.Snapshot.Version);

        // Database verification
        var transition = await db.CharacterStateTransitions.SingleAsync(t => t.CharacterId == charId);
        Assert.Equal(executionId, transition.ExecutionId);
        Assert.Equal(-24.0m, transition.HungerDelta);
        Assert.Equal(1, transition.VersionBefore);
        Assert.Equal(2, transition.VersionAfter);
    }

    [Theory]
    [InlineData(MotivationType.HungerDriven)]
    [InlineData(MotivationType.RestorationDriven)]
    [InlineData(MotivationType.StressReliefDriven)]
    [InlineData(MotivationType.ConnectionDriven)]
    [InlineData(MotivationType.ComfortDriven)]
    [InlineData(MotivationType.SafetyDriven)]
    public async Task ExecuteAsync_PreservesExactMotivationType(MotivationType expectedMotivation)
    {
        var charId = await SeedCharacterStateAsync();
        var executionId = Guid.NewGuid();

        var proposal = new CharacterActionProposal(
            type: ActionType.Rest,
            intensity: 0.5,
            sourceIntent: IntentType.SeekRest,
            motivation: expectedMotivation,
            stateVersion: 1
        );

        var context = new CharacterActionExecutionContext(executionId);

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.ExecuteAsync(charId, proposal, context);

        Assert.Equal(CharacterActionExecutionStatus.Applied, result.Status);
        Assert.Equal(expectedMotivation, result.Motivation);
    }

    #endregion

    #region 2. Idempotency & Idempotency Conflict Tests

    [Fact]
    public async Task ExecuteAsync_SameExecutionIdCalledTwice_SuppressesDuplicateAndReturnsAlreadyExecuted()
    {
        var charId = await SeedCharacterStateAsync(hunger: 70m);
        var executionId = Guid.NewGuid();

        var proposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.5,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        var context = new CharacterActionExecutionContext(executionId);

        await using var db1 = new CoreDbContext(_options);
        var service1 = CreateService(db1);
        var result1 = await service1.ExecuteAsync(charId, proposal, context);

        Assert.Equal(CharacterActionExecutionStatus.Applied, result1.Status);
        Assert.Equal(55m, result1.Snapshot!.Hunger); // 70 - 15 = 55
        Assert.Equal(2, result1.StateVersionAfter);

        // Second call with same executionId
        await using var db2 = new CoreDbContext(_options);
        var service2 = CreateService(db2);
        var result2 = await service2.ExecuteAsync(charId, proposal, context);

        Assert.Equal(CharacterActionExecutionStatus.AlreadyExecuted, result2.Status);
        Assert.True(result2.IsSuccess);
        Assert.False(result2.IsApplied);
        Assert.True(result2.IsDuplicateSuppressed);
        Assert.Equal(2, result2.StateVersionAfter);
        Assert.Equal(55m, result2.Snapshot!.Hunger); // State NOT mutated second time

        // Exactly one transition record in DB
        await using var verifyDb = new CoreDbContext(_options);
        var count = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ExecuteAsync_SameExecutionIdWithDifferentPayload_ReturnsIdempotencyConflict()
    {
        var charId = await SeedCharacterStateAsync(hunger: 80m, energy: 50m);
        var executionId = Guid.NewGuid();

        var proposalA = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.5,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        var proposalB = new CharacterActionProposal(
            type: ActionType.Rest,
            intensity: 0.8,
            sourceIntent: IntentType.SeekRest,
            motivation: MotivationType.RestorationDriven,
            stateVersion: 1
        );

        var context = new CharacterActionExecutionContext(executionId);

        // Execute A
        await using var db1 = new CoreDbContext(_options);
        var service1 = CreateService(db1);
        var resultA = await service1.ExecuteAsync(charId, proposalA, context);
        Assert.Equal(CharacterActionExecutionStatus.Applied, resultA.Status);

        // Execute B with SAME executionId
        await using var db2 = new CoreDbContext(_options);
        var service2 = CreateService(db2);
        var resultB = await service2.ExecuteAsync(charId, proposalB, context);

        Assert.Equal(CharacterActionExecutionStatus.IdempotencyConflict, resultB.Status);
        Assert.False(resultB.IsSuccess);

        // Ensure state only reflects Action A
        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(65m, state.Hunger); // 80 - 15 = 65 (from A)
        Assert.Equal(50m, state.Energy); // Energy untouched (B was rejected)
        Assert.Equal(2, state.Version);
    }

    #endregion

    #region 3. Concurrency Tests

    [Fact]
    public async Task ExecuteAsync_TenConcurrentWorkersWithSameExecutionId_ExecutesExactlyOnce()
    {
        var charId = await SeedCharacterStateAsync(hunger: 90m);
        var executionId = Guid.NewGuid();

        var proposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 1.0,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        var context = new CharacterActionExecutionContext(executionId);

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var service = CreateService(workerDb);
            return await service.ExecuteAsync(charId, proposal, context);
        });

        var results = await Task.WhenAll(tasks);

        var appliedCount = results.Count(r => r.Status == CharacterActionExecutionStatus.Applied);
        var suppressedCount = results.Count(r => r.Status == CharacterActionExecutionStatus.AlreadyExecuted);

        Assert.Equal(1, appliedCount);
        Assert.Equal(9, suppressedCount);

        // Database checks
        await using var verifyDb = new CoreDbContext(_options);
        var transitions = await verifyDb.CharacterStateTransitions.Where(t => t.CharacterId == charId).ToListAsync();
        Assert.Single(transitions);

        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.Equal(60m, state.Hunger); // 90 - 30 = 60
        Assert.Equal(2, state.Version);
    }

    [Fact]
    public async Task ExecuteAsync_TwentyConcurrentWorkersWithDistinctExecutionIds_RespectsOptimisticConcurrency()
    {
        var charId = await SeedCharacterStateAsync(energy: 10m);

        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var executionId = Guid.NewGuid();
            var proposal = new CharacterActionProposal(
                type: ActionType.Rest,
                intensity: 0.1,
                sourceIntent: IntentType.SeekRest,
                motivation: MotivationType.RestorationDriven,
                stateVersion: 1
            );

            var context = new CharacterActionExecutionContext(executionId);

            await using var workerDb = new CoreDbContext(_options);
            var service = CreateService(workerDb);
            return await service.ExecuteAsync(charId, proposal, context);
        });

        var results = await Task.WhenAll(tasks);

        // Every worker must get either Applied or ConcurrencyConflict (no silent errors or corruption)
        Assert.All(results, r =>
        {
            Assert.True(
                r.Status == CharacterActionExecutionStatus.Applied ||
                r.Status == CharacterActionExecutionStatus.ConcurrencyConflict,
                $"Unexpected status: {r.Status}");
        });

        var appliedCount = results.Count(r => r.Status == CharacterActionExecutionStatus.Applied);
        Assert.True(appliedCount >= 1, "At least one worker must have succeeded.");

        await using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.SingleAsync(s => s.CharacterId == charId);

        // State version progression must strictly equal initial version (1) + number of applied transitions
        Assert.Equal(1 + appliedCount, state.Version);

        var transitionCount = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(appliedCount, transitionCount);
    }

    #endregion

    #region 4. Input Validation & Error Handling Tests

    [Fact]
    public async Task ExecuteAsync_RejectsInvalidInputs()
    {
        var charId = Guid.NewGuid();
        var proposal = new CharacterActionProposal(
            ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, 1);
        var context = new CharacterActionExecutionContext(Guid.NewGuid());

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        // Empty CharacterId
        var res1 = await service.ExecuteAsync(Guid.Empty, proposal, context);
        Assert.Equal(CharacterActionExecutionStatus.InvalidProposal, res1.Status);

        // Empty ExecutionId
        var res2 = await service.ExecuteAsync(charId, proposal, new CharacterActionExecutionContext(Guid.Empty));
        Assert.Equal(CharacterActionExecutionStatus.InvalidProposal, res2.Status);

        // Null context
        var res3 = await service.ExecuteAsync(charId, proposal, null!);
        Assert.Equal(CharacterActionExecutionStatus.InvalidProposal, res3.Status);

        // Null proposal
        var res4 = await service.ExecuteAsync(charId, null!, context);
        Assert.Equal(CharacterActionExecutionStatus.InvalidProposal, res4.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNotFound_WhenCharacterDoesNotExist()
    {
        var nonExistentCharId = Guid.NewGuid();
        var proposal = new CharacterActionProposal(
            ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, 1);
        var context = new CharacterActionExecutionContext(Guid.NewGuid());

        await using var db = new CoreDbContext(_options);
        var service = CreateService(db);

        var result = await service.ExecuteAsync(nonExistentCharId, proposal, context);

        Assert.Equal(CharacterActionExecutionStatus.NotFound, result.Status);
        Assert.False(result.IsSuccess);
    }

    #endregion
}
