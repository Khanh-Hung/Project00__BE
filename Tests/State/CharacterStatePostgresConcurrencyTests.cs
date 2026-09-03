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
using Xunit.Abstractions;

namespace Tests.State;

public sealed class CharacterStatePostgresConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public CharacterStatePostgresConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static string GetPostgresConnectionString()
    {
        var envConn = Environment.GetEnvironmentVariable("ConnectionStrings__CoreConnection");
        if (!string.IsNullOrWhiteSpace(envConn)) return envConn;

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            foreach (var fileName in new[] { "appsettings.Development.json", "appsettings.json" })
            {
                var path = Path.Combine(dir.FullName, fileName);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("ConnectionStrings", out var connSection) &&
                        connSection.TryGetProperty("CoreConnection", out var connProp))
                    {
                        var val = connProp.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException("PostgreSQL integration tests require a live PostgreSQL connection string. Provide ConnectionStrings__CoreConnection or configure appsettings.json.");
    }

    private static DbContextOptions<CoreDbContext> CreatePostgresOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    private static async Task EnsureTablesExistAsync(CoreDbContext db)
    {
        var sql = @"
CREATE TABLE IF NOT EXISTS ""CharacterStates"" (
    ""Id"" uuid NOT NULL PRIMARY KEY,
    ""CharacterId"" uuid NOT NULL,
    ""Hunger"" numeric(5,2) NOT NULL,
    ""Energy"" numeric(5,2) NOT NULL,
    ""Mood"" numeric(5,2) NOT NULL,
    ""Stress"" numeric(5,2) NOT NULL,
    ""SocialNeed"" numeric(5,2) NOT NULL,
    ""Comfort"" numeric(5,2) NOT NULL,
    ""LastEvolvedAtUtc"" timestamp with time zone NOT NULL,
    ""Version"" integer NOT NULL,
    ""CreatedAt"" timestamp with time zone NOT NULL,
    ""CreatedBy"" text NULL,
    ""UpdatedAt"" timestamp with time zone NULL,
    ""UpdatedBy"" text NULL,
    ""IsSoftDeleted"" boolean NOT NULL DEFAULT FALSE,
    ""DeletedAt"" timestamp with time zone NULL,
    ""DeletedBy"" text NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CharacterStates_CharacterId"" ON ""CharacterStates"" (""CharacterId"");

ALTER TABLE ""CharacterStates"" ADD COLUMN IF NOT EXISTS ""CreatedBy"" text NULL;
ALTER TABLE ""CharacterStates"" ADD COLUMN IF NOT EXISTS ""UpdatedBy"" text NULL;
ALTER TABLE ""CharacterStates"" ADD COLUMN IF NOT EXISTS ""IsSoftDeleted"" boolean NOT NULL DEFAULT FALSE;
ALTER TABLE ""CharacterStates"" ADD COLUMN IF NOT EXISTS ""DeletedAt"" timestamp with time zone NULL;
ALTER TABLE ""CharacterStates"" ADD COLUMN IF NOT EXISTS ""DeletedBy"" text NULL;

CREATE TABLE IF NOT EXISTS ""CharacterStateTransitions"" (
    ""Id"" uuid NOT NULL PRIMARY KEY,
    ""CharacterId"" uuid NOT NULL,
    ""ExecutionId"" uuid NOT NULL,
    ""SourceType"" character varying(50) NOT NULL,
    ""SourceId"" character varying(100) NULL,
    ""TransitionFingerprint"" character varying(64) NOT NULL DEFAULT '',
    ""HungerDelta"" numeric(5,2) NOT NULL,
    ""EnergyDelta"" numeric(5,2) NOT NULL,
    ""MoodDelta"" numeric(5,2) NOT NULL,
    ""StressDelta"" numeric(5,2) NOT NULL,
    ""SocialNeedDelta"" numeric(5,2) NOT NULL,
    ""ComfortDelta"" numeric(5,2) NOT NULL,
    ""VersionBefore"" integer NOT NULL DEFAULT 1,
    ""VersionAfter"" integer NOT NULL DEFAULT 2,
    ""AppliedAtUtc"" timestamp with time zone NOT NULL DEFAULT NOW(),
    ""CreatedAt"" timestamp with time zone NOT NULL,
    ""CreatedBy"" text NULL,
    ""UpdatedAt"" timestamp with time zone NULL,
    ""UpdatedBy"" text NULL,
    ""IsSoftDeleted"" boolean NOT NULL DEFAULT FALSE,
    ""DeletedAt"" timestamp with time zone NULL,
    ""DeletedBy"" text NULL
);

ALTER TABLE ""CharacterStateTransitions"" DROP COLUMN IF EXISTS ""Reason"";
ALTER TABLE ""CharacterStateTransitions"" DROP COLUMN IF EXISTS ""TransitionedAtUtc"";
ALTER TABLE ""CharacterStateTransitions"" ADD COLUMN IF NOT EXISTS ""VersionBefore"" integer NOT NULL DEFAULT 1;
ALTER TABLE ""CharacterStateTransitions"" ADD COLUMN IF NOT EXISTS ""VersionAfter"" integer NOT NULL DEFAULT 2;
ALTER TABLE ""CharacterStateTransitions"" ADD COLUMN IF NOT EXISTS ""AppliedAtUtc"" timestamp with time zone NOT NULL DEFAULT NOW();
ALTER TABLE ""CharacterStateTransitions"" ADD COLUMN IF NOT EXISTS ""CreatedBy"" text NULL;
ALTER TABLE ""CharacterStateTransitions"" ADD COLUMN IF NOT EXISTS ""UpdatedBy"" text NULL;
ALTER TABLE ""CharacterStateTransitions"" ADD COLUMN IF NOT EXISTS ""IsSoftDeleted"" boolean NOT NULL DEFAULT FALSE;
ALTER TABLE ""CharacterStateTransitions"" ADD COLUMN IF NOT EXISTS ""DeletedAt"" timestamp with time zone NULL;
ALTER TABLE ""CharacterStateTransitions"" ADD COLUMN IF NOT EXISTS ""DeletedBy"" text NULL;
ALTER TABLE ""CharacterStateTransitions"" ADD COLUMN IF NOT EXISTS ""TransitionFingerprint"" character varying(64) NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS ""IX_CharacterStateTransitions_CharacterId_AppliedAtUtc"" ON ""CharacterStateTransitions"" (""CharacterId"", ""AppliedAtUtc"");
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CharacterStateTransitions_CharacterId_ExecutionId"" ON ""CharacterStateTransitions"" (""CharacterId"", ""ExecutionId"");
";
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    [Fact]
    public async Task Postgres_10Workers_SameExecutionId_OnlyOneApplies_NineSuppressed()
    {
        var connStr = GetPostgresConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connStr), "PostgreSQL integration tests require a live PostgreSQL connection.");

        var options = CreatePostgresOptions(connStr);
        var charId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Setup: seed initial state in real PostgreSQL
        await using (var seedDb = new CoreDbContext(options))
        {
            await EnsureTablesExistAsync(seedDb);
            var version = await seedDb.Database.SqlQueryRaw<string>(@"SELECT version() AS ""Value""").FirstOrDefaultAsync();
            _output.WriteLine($"[PostgreSQL Server Verified] Version: {version}");

            var state = new CharacterState(charId, now, 80m, 90m, 70m, 10m, 20m, 95m);
            await seedDb.CharacterStates.AddAsync(state);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            // Execute 10 concurrent workers against 10 distinct DbContext instances with IDENTICAL logical payload
            var tasks = Enumerable.Range(0, 10).Select(async _ =>
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
                    SourceId: "same-source",
                    Reason: "Concurrent identical execution"
                );

                return await service.TransitionAsync(charId, delta, context, now);
            });

            var results = await Task.WhenAll(tasks);

            // Assert: Exactly ONE worker applied the transition, 9 received AlreadyApplied
            var appliedCount = results.Count(r => r.Status == StateTransitionResultStatus.Applied);
            var alreadyAppliedCount = results.Count(r => r.Status == StateTransitionResultStatus.AlreadyApplied);
            var conflictCount = results.Count(r => r.Status == StateTransitionResultStatus.IdempotencyConflict);
            var concurrencyCount = results.Count(r => r.Status == StateTransitionResultStatus.ConcurrencyConflict);

            Assert.Equal(1, appliedCount);
            Assert.Equal(9, alreadyAppliedCount);
            Assert.Equal(0, conflictCount);
            Assert.Equal(0, concurrencyCount);

            // Verify database state in PostgreSQL
            await using (var verifyDb = new CoreDbContext(options))
            {
                var finalState = await verifyDb.CharacterStates.AsNoTracking().FirstOrDefaultAsync(s => s.CharacterId == charId);
                Assert.NotNull(finalState);
                Assert.Equal(2, finalState.Version); // Initial version = 1, incremented once to 2
                Assert.Equal(75m, finalState.Hunger); // 80 - 5 = 75, not double subtracted
                Assert.Equal(80m, finalState.Energy); // 90 - 10 = 80

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
    public async Task Postgres_SameExecutionId_DifferentPayload_ReturnsIdempotencyConflict()
    {
        var connStr = GetPostgresConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connStr), "PostgreSQL integration tests require a live PostgreSQL connection.");

        var options = CreatePostgresOptions(connStr);
        var charId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var seedDb = new CoreDbContext(options))
        {
            await EnsureTablesExistAsync(seedDb);
            var state = new CharacterState(charId, now, 80m, 90m, 70m, 10m, 20m, 95m);
            await seedDb.CharacterStates.AddAsync(state);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db1 = new CoreDbContext(options);
            var service1 = new CharacterStateTransitionService(db1, NullLogger<CharacterStateTransitionService>.Instance);

            var delta1 = new CharacterStateDelta(hungerDelta: -5m, energyDelta: -10m, moodDelta: 5m, stressDelta: -2m, socialNeedDelta: 0m, comfortDelta: 0m);
            var context1 = new StateTransitionContext(ExecutionId: executionId, SourceType: "PostgresIntegrationTest", SourceId: "same-source", Reason: "First payload");

            var result1 = await service1.TransitionAsync(charId, delta1, context1, now);
            Assert.Equal(StateTransitionResultStatus.Applied, result1.Status);

            // Second attempt with same ExecutionId but DIFFERENT delta payload
            await using var db2 = new CoreDbContext(options);
            var service2 = new CharacterStateTransitionService(db2, NullLogger<CharacterStateTransitionService>.Instance);

            var delta2 = new CharacterStateDelta(hungerDelta: +20m, energyDelta: -50m, moodDelta: -10m, stressDelta: 15m, socialNeedDelta: 5m, comfortDelta: -10m);
            var context2 = new StateTransitionContext(ExecutionId: executionId, SourceType: "PostgresIntegrationTest", SourceId: "same-source", Reason: "Conflicting payload");

            var result2 = await service2.TransitionAsync(charId, delta2, context2, now);
            Assert.Equal(StateTransitionResultStatus.IdempotencyConflict, result2.Status);

            // Verify database state: only 1 transition recorded, state not modified by second call
            await using var verifyDb = new CoreDbContext(options);
            var finalState = await verifyDb.CharacterStates.AsNoTracking().FirstOrDefaultAsync(s => s.CharacterId == charId);
            Assert.NotNull(finalState);
            Assert.Equal(2, finalState.Version);
            Assert.Equal(75m, finalState.Hunger); // Still 75, not 75 + 20

            var transitions = await verifyDb.CharacterStateTransitions.AsNoTracking()
                .Where(t => t.CharacterId == charId && t.ExecutionId == executionId)
                .ToListAsync();
            Assert.Single(transitions);
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

    [Fact]
    public async Task Postgres_10Workers_DifferentExecutionId_OptimisticConcurrency_SerializesConsistently()
    {
        var connStr = GetPostgresConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connStr), "PostgreSQL integration tests require a live PostgreSQL connection.");

        var options = CreatePostgresOptions(connStr);
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var seedDb = new CoreDbContext(options))
        {
            await EnsureTablesExistAsync(seedDb);
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

            // In our service contract, concurrent stale writers receive ConcurrencyConflict
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
