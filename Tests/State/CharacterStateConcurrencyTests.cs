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

public sealed class CharacterStateConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterStateConcurrencyTests()
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
    public async Task TestA_TenConcurrentWorkers_InitializingSameCharacterId_ResultsInExactlyOneRowInDb()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var tasks = Enumerable.Range(1, 10).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var transitionService = new CharacterStateTransitionService(workerDb, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(workerDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

            return await stateService.GetOrCreateInitialStateAsync(charId, now);
        });

        var results = await Task.WhenAll(tasks);

        // Every worker received a valid initial snapshot
        Assert.All(results, s =>
        {
            Assert.NotNull(s);
            Assert.Equal(80, s.Energy);
            Assert.Equal(1, s.Version);
        });

        // Exactly 1 row in database
        using var verifyDb = new CoreDbContext(_options);
        var count = await verifyDb.CharacterStates.CountAsync(s => s.CharacterId == charId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TestB_TenConcurrentWorkers_ApplyingSameExecutionId_ProducesOneAppliedAndNineSuppressed()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Initialize state
        using (var setupDb = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(setupDb, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(setupDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            await stateService.GetOrCreateInitialStateAsync(charId, now);
        }

        var sharedExecutionId = Guid.NewGuid();
        var delta = new CharacterStateDelta(hungerDelta: 15m);
        var context = new StateTransitionContext(sharedExecutionId, "Reaction", "event-test");

        var tasks = Enumerable.Range(1, 10).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var transitionService = new CharacterStateTransitionService(workerDb, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(workerDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

            return await stateService.ApplyDeltaAsync(charId, delta, context, now);
        });

        var results = await Task.WhenAll(tasks);

        int appliedCount = results.Count(r => r.Status == StateTransitionResultStatus.Applied);
        int suppressedCount = results.Count(r => r.Status == StateTransitionResultStatus.AlreadyApplied);

        Assert.Equal(1, appliedCount);
        Assert.Equal(9, suppressedCount);

        // Verify DB only recorded 1 transition and applied hunger once (+15)
        using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.Equal(35m, state.Hunger); // 20 + 15
        Assert.Equal(2, state.Version);

        var transitionCount = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId && t.ExecutionId == sharedExecutionId);
        Assert.Equal(1, transitionCount);
    }

    [Fact]
    public async Task TestC_TenConcurrentWorkers_ApplyingDistinctExecutionIds_WithRetry_AppliesAllDeltasWithoutLoss()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var setupDb = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(setupDb, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(setupDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            await stateService.GetOrCreateInitialStateAsync(charId, now);
        }

        var tasks = Enumerable.Range(1, 10).Select(async i =>
        {
            var execId = Guid.NewGuid();
            var delta = new CharacterStateDelta(hungerDelta: 2m); // 10 workers * 2 = +20 total
            var context = new StateTransitionContext(execId, "Activity", $"act-{i}");

            const int maxRetries = 10;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                await using var workerDb = new CoreDbContext(_options);
                var transitionService = new CharacterStateTransitionService(workerDb, NullLogger<CharacterStateTransitionService>.Instance);
                var stateService = new CharacterStateService(workerDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

                var res = await stateService.ApplyDeltaAsync(charId, delta, context, now);
                if (res.IsSuccess)
                {
                    return res;
                }

                await Task.Delay(Random.Shared.Next(5, 25));
            }

            throw new InvalidOperationException($"Worker {i} could not apply delta within retry limit.");
        });

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.IsSuccess));

        using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.Equal(40m, state.Hunger); // 20 initial + 10 * 2 = 40
        Assert.Equal(11, state.Version); // 1 initial + 10 increments

        var totalTransitions = await verifyDb.CharacterStateTransitions.CountAsync(t => t.CharacterId == charId);
        Assert.Equal(10, totalTransitions);
    }

    [Fact]
    public async Task TestD_TenConcurrentWorkers_EvolvingToSameTimestamp_AppliesEvolutionOnlyOnce()
    {
        var charId = Guid.NewGuid();
        var t0 = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddHours(2); // 2 hours: Hunger rate = +4/hr => +8 total

        using (var setupDb = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(setupDb, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(setupDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            await stateService.GetOrCreateInitialStateAsync(charId, t0);
        }

        var tasks = Enumerable.Range(1, 10).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var transitionService = new CharacterStateTransitionService(workerDb, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(workerDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);

            return await stateService.EvolveToAsync(charId, t1);
        });

        var results = await Task.WhenAll(tasks);

        // All workers report success (either Applied or AlreadyApplied)
        Assert.All(results, r => Assert.True(r.IsSuccess));

        int appliedCount = results.Count(r => r.Status == StateTransitionResultStatus.Applied);
        int suppressedCount = results.Count(r => r.Status == StateTransitionResultStatus.AlreadyApplied);
        Assert.Equal(1, appliedCount);
        Assert.Equal(9, suppressedCount);

        using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.FirstAsync(s => s.CharacterId == charId);

        // Hunger must have evolved by exactly +8 (NOT 10 * 8 = 80!)
        Assert.Equal(28m, state.Hunger); // 20 + 8 = 28
        Assert.Equal(t1, state.LastEvolvedAtUtc);
    }

    [Fact]
    public async Task TestE_TemporalEvolutionRace_WorkerA_1300_vs_WorkerB_1400_EnsuresLastEvolvedAtNeverRegresses()
    {
        var charId = Guid.NewGuid();
        var t0 = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        var t13 = t0.AddHours(1); // 13:00 (+1 hr => Hunger +4 = 24)
        var t14 = t0.AddHours(2); // 14:00 (+2 hr => Hunger +8 = 28)

        using (var setupDb = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(setupDb, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(setupDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            await stateService.GetOrCreateInitialStateAsync(charId, t0);
        }

        // Run concurrent workers targeting 13:00 and 14:00 with retry loop
        static async Task<StateTransitionResult> EvolveWithRetryAsync(DbContextOptions<CoreDbContext> options, Guid charId, DateTime targetTime)
        {
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                try
                {
                    await using var workerDb = new CoreDbContext(options);
                    var transitionService = new CharacterStateTransitionService(workerDb, NullLogger<CharacterStateTransitionService>.Instance);
                    var stateService = new CharacterStateService(workerDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
                    var res = await stateService.EvolveToAsync(charId, targetTime);
                    if (res.IsSuccess) return res;
                }
                catch (Exception) when (attempt < 10)
                {
                }

                await Task.Delay(Random.Shared.Next(5, 30));
            }
            throw new InvalidOperationException($"Failed to evolve to {targetTime} within retries.");
        }

        var task13 = Task.Run(() => EvolveWithRetryAsync(_options, charId, t13));
        var task14 = Task.Run(() => EvolveWithRetryAsync(_options, charId, t14));

        var results = await Task.WhenAll(task13, task14);

        // Both workers should succeed
        Assert.All(results, r => Assert.True(r.IsSuccess));

        // State must NEVER have regressed to 13:00
        using var verifyDb = new CoreDbContext(_options);
        var finalState = await verifyDb.CharacterStates.FirstAsync(s => s.CharacterId == charId);

        Assert.Equal(t14, finalState.LastEvolvedAtUtc);
        Assert.Equal(28m, finalState.Hunger); // 20 + 8 = 28

        // If another worker subsequently tries to evolve to 13:00, it must be suppressed without regression
        await using (var staleDb = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(staleDb, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(staleDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            var staleResult = await stateService.EvolveToAsync(charId, t13);

            Assert.Equal(StateTransitionResultStatus.AlreadyApplied, staleResult.Status);
        }

        var reloaded = await verifyDb.CharacterStates.AsNoTracking().FirstAsync(s => s.CharacterId == charId);
        Assert.Equal(t14, reloaded.LastEvolvedAtUtc);
        Assert.Equal(28m, reloaded.Hunger);
    }

    [Fact]
    public async Task TestF_PayloadConsistency_SameExecutionId_DifferentPayload_RejectsWithIdempotencyConflict()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var setupDb = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(setupDb, NullLogger<CharacterStateTransitionService>.Instance);
            var stateService = new CharacterStateService(setupDb, transitionService, new CharacterStateEvolutionPolicy(), NullLogger<CharacterStateService>.Instance);
            await stateService.GetOrCreateInitialStateAsync(charId, now);
        }

        var executionId = Guid.NewGuid();
        var delta1 = new CharacterStateDelta(hungerDelta: 10m);
        var delta2 = new CharacterStateDelta(hungerDelta: 50m); // conflicting payload!

        var context = new StateTransitionContext(executionId, "Reaction", "event-payload-test");

        // 1. First execution succeeds
        using (var db1 = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db1, NullLogger<CharacterStateTransitionService>.Instance);
            var result1 = await transitionService.TransitionAsync(charId, delta1, context, now);
            Assert.Equal(StateTransitionResultStatus.Applied, result1.Status);
        }

        // 2. Second execution with SAME ExecutionId but DIFFERENT payload must be rejected
        using (var db2 = new CoreDbContext(_options))
        {
            var transitionService = new CharacterStateTransitionService(db2, NullLogger<CharacterStateTransitionService>.Instance);
            var result2 = await transitionService.TransitionAsync(charId, delta2, context, now);

            Assert.Equal(StateTransitionResultStatus.IdempotencyConflict, result2.Status);
            Assert.Contains("different payload", result2.Message ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // Verify DB only applied the first delta (+10, not +10+50 or +50)
        using var verifyDb = new CoreDbContext(_options);
        var state = await verifyDb.CharacterStates.FirstAsync(s => s.CharacterId == charId);
        Assert.Equal(30m, state.Hunger); // 20 + 10 = 30
        Assert.Equal(2, state.Version);
    }
}
