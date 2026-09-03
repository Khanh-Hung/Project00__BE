using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Application.Common;
using Application.Enums;
using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.State;

public sealed class CharacterStatePostgresConcurrencyTests
{
    private static string? GetPostgresConnectionString()
    {
        var envConn = Environment.GetEnvironmentVariable("ConnectionStrings__CoreConnection");
        if (!string.IsNullOrWhiteSpace(envConn)) return envConn;

        var devSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "appsettings.Development.json");
        if (File.Exists(devSettingsPath))
        {
            var json = File.ReadAllText(devSettingsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ConnectionStrings", out var connSection) &&
                connSection.TryGetProperty("CoreConnection", out var connProp))
            {
                return connProp.GetString();
            }
        }
        return null;
    }

    private static DbContextOptions<CoreDbContext> CreatePostgresOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    [Fact]
    public async Task Postgres_10Workers_SameExecutionId_OnlyOneApplies_NineSuppressed()
    {
        var connStr = GetPostgresConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            // Skip when running without live PostgreSQL instance
            return;
        }

        var options = CreatePostgresOptions(connStr);
        var charId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Setup: seed initial state in real PostgreSQL
        await using (var seedDb = new CoreDbContext(options))
        {
            var state = new CharacterState(charId, now, 80m, 90m, 70m, 10m, 20m, 95m);
            await seedDb.CharacterStates.AddAsync(state);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            // Execute 10 concurrent workers against 10 distinct DbContext instances
            var tasks = Enumerable.Range(0, 10).Select(async workerId =>
            {
                await using var db = new CoreDbContext(options);
                var service = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);

                var delta = new CharacterStateDelta(
                    hungerDelta: -5m,
                    energyDelta: -10m,
                    moodDelta: 5m,
                    stressDelta: -2m,
                    socialNeedDelta: 0m,
                    comfortDelta: 0m
                );

                var context = new StateTransitionContext(
                    ExecutionId: executionId,
                    SourceType: "PostgresIntegrationTest",
                    SourceId: workerId.ToString(),
                    Reason: $"Worker {workerId} concurrent run"
                );

                return await service.TransitionAsync(charId, delta, context, now);
            });

            var results = await Task.WhenAll(tasks);

            // Assert: Exactly ONE worker applied the transition, 9 received AlreadyApplied
            var appliedCount = results.Count(r => r.Status == StateTransitionResultStatus.Applied);
            var alreadyAppliedCount = results.Count(r => r.Status == StateTransitionResultStatus.AlreadyApplied);

            Assert.Equal(1, appliedCount);
            Assert.Equal(9, alreadyAppliedCount);

            // Verify database state in PostgreSQL
            await using (var verifyDb = new CoreDbContext(options))
            {
                var finalState = await verifyDb.CharacterStates.AsNoTracking().FirstOrDefaultAsync(s => s.CharacterId == charId);
                Assert.NotNull(finalState);
                Assert.Equal(2, finalState.Version); // Initial version = 1, incremented once to 2

                var transitions = await verifyDb.CharacterStateTransitions.AsNoTracking()
                    .Where(t => t.CharacterId == charId && t.ExecutionId == executionId)
                    .ToListAsync();

                Assert.Single(transitions);
            }
        }
        finally
        {
            // Teardown test records
            await using var cleanupDb = new CoreDbContext(options);
            var transitions = await cleanupDb.CharacterStateTransitions.Where(t => t.CharacterId == charId).ToListAsync();
            cleanupDb.CharacterStateTransitions.RemoveRange(transitions);

            var states = await cleanupDb.CharacterStates.Where(s => s.CharacterId == charId).ToListAsync();
            cleanupDb.CharacterStates.RemoveRange(states);

            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Postgres_10Workers_DifferentExecutionId_OptimisticConcurrency_SerializesConsistently()
    {
        var connStr = GetPostgresConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            return;
        }

        var options = CreatePostgresOptions(connStr);
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var seedDb = new CoreDbContext(options))
        {
            var state = new CharacterState(charId, now, 50m, 50m, 50m, 50m, 50m, 50m);
            await seedDb.CharacterStates.AddAsync(state);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            var tasks = Enumerable.Range(0, 10).Select(async workerId =>
            {
                await using var db = new CoreDbContext(options);
                var service = new CharacterStateTransitionService(db, NullLogger<CharacterStateTransitionService>.Instance);

                var delta = new CharacterStateDelta(
                    hungerDelta: 1m,
                    energyDelta: -1m,
                    moodDelta: 0m,
                    stressDelta: 0m,
                    socialNeedDelta: 0m,
                    comfortDelta: 0m
                );

                var context = new StateTransitionContext(
                    ExecutionId: Guid.NewGuid(),
                    SourceType: "PostgresOCC",
                    SourceId: workerId.ToString(),
                    Reason: $"Worker {workerId} distinct transition"
                );

                return await service.TransitionAsync(charId, delta, context, now.AddMinutes(workerId));
            });

            var results = await Task.WhenAll(tasks);

            // Any successful transaction must increment version; any concurrency conflict is correctly flagged
            var successful = results.Where(r => r.Status == StateTransitionResultStatus.Applied).ToList();
            var concurrencyConflicts = results.Where(r => r.Status == StateTransitionResultStatus.ConcurrencyConflict).ToList();

            Assert.NotEmpty(successful);
            Assert.Equal(10, successful.Count + concurrencyConflicts.Count);

            // In PostgreSQL, verify final state version matches 1 + successful count
            await using (var verifyDb = new CoreDbContext(options))
            {
                var finalState = await verifyDb.CharacterStates.AsNoTracking().FirstOrDefaultAsync(s => s.CharacterId == charId);
                Assert.NotNull(finalState);
                Assert.Equal(1 + successful.Count, finalState.Version);

                var totalTransitions = await verifyDb.CharacterStateTransitions.AsNoTracking()
                    .CountAsync(t => t.CharacterId == charId);
                Assert.Equal(successful.Count, totalTransitions);
            }
        }
        finally
        {
            await using var cleanupDb = new CoreDbContext(options);
            var transitions = await cleanupDb.CharacterStateTransitions.Where(t => t.CharacterId == charId).ToListAsync();
            cleanupDb.CharacterStateTransitions.RemoveRange(transitions);

            var states = await cleanupDb.CharacterStates.Where(s => s.CharacterId == charId).ToListAsync();
            cleanupDb.CharacterStates.RemoveRange(states);

            await cleanupDb.SaveChangesAsync();
        }
    }
}
