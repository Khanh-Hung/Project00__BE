using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.LifeSimulation;
using Application.Interfaces;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.LifeSimulation;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Tests.LifeSimulation;

public sealed class CharacterOutboxPostgresConstraintTests
{
    private readonly ITestOutputHelper _output;

    public CharacterOutboxPostgresConstraintTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static string? TryGetPostgresConnectionString()
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

        return null;
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
CREATE TABLE IF NOT EXISTS ""CharacterOutboxMessages"" (
    ""Id"" uuid NOT NULL PRIMARY KEY,
    ""EventId"" uuid NOT NULL,
    ""CharacterId"" uuid NOT NULL,
    ""EventType"" character varying(100) NOT NULL,
    ""PayloadJson"" jsonb NOT NULL,
    ""Fingerprint"" character varying(64) NOT NULL,
    ""OccurredAtUtc"" timestamp with time zone NOT NULL,
    ""CreatedAtUtc"" timestamp with time zone NOT NULL,
    ""Status"" character varying(50) NOT NULL,
    ""AttemptCount"" integer NOT NULL DEFAULT 0,
    ""MaxRetries"" integer NOT NULL DEFAULT 3,
    ""ProcessedAtUtc"" timestamp with time zone NULL,
    ""LastError"" character varying(2000) NULL,
    ""Version"" bigint NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CharacterOutboxMessages_EventId""
ON ""CharacterOutboxMessages"" (""EventId"");

CREATE INDEX IF NOT EXISTS ""IX_CharacterOutboxMessages_Status_OccurredAtUtc_CreatedAtUtc""
ON ""CharacterOutboxMessages"" (""Status"", ""OccurredAtUtc"", ""CreatedAtUtc"");

CREATE INDEX IF NOT EXISTS ""IX_CharacterOutboxMessages_CharacterId_Status_OccurredAtUtc""
ON ""CharacterOutboxMessages"" (""CharacterId"", ""Status"", ""OccurredAtUtc"");
";
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    [Fact]
    public async Task Postgres_ModelMetadata_VerifiesUniqueIndexOnEventId()
    {
        var connStr = TryGetPostgresConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            _output.WriteLine("Skipping Postgres test because connection string is not available.");
            return;
        }

        var options = CreatePostgresOptions(connStr);
        using var db = new CoreDbContext(options);

        var entityType = db.Model.FindEntityType(typeof(CharacterOutboxMessage));
        Assert.NotNull(entityType);

        var eventIdIndex = entityType.GetIndexes()
            .FirstOrDefault(idx => idx.Properties.Any(p => p.Name == "EventId"));

        Assert.NotNull(eventIdIndex);
        Assert.True(eventIdIndex.IsUnique);
    }

    [Fact]
    public async Task Postgres_LiveDb_EnforcesUniqueEventId_AndIdempotentAddOrGet()
    {
        var connStr = TryGetPostgresConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            _output.WriteLine("Skipping Postgres test because connection string is not available.");
            return;
        }

        var options = CreatePostgresOptions(connStr);
        using var db = new CoreDbContext(options);

        try
        {
            await EnsureTablesExistAsync(db);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Could not connect to PostgreSQL live database: {ex.Message}. Skipping test.");
            return;
        }

        var sharedEventId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var repo1 = new CharacterOutboxRepository(db);

        var msg1 = new CharacterOutboxMessage(
            sharedEventId, charId, "ActivityStarted", "{\"pg\":true}", "fp_pg", now);
        var msg2 = new CharacterOutboxMessage(
            sharedEventId, charId, "ActivityStarted", "{\"pg\":true}", "fp_pg", now);

        var saved1 = await repo1.AddOrGetAsync(msg1);
        Assert.NotNull(saved1);

        // Second call with same eventId + same payload => returns existing
        var saved2 = await repo1.AddOrGetAsync(msg2);
        Assert.Equal(saved1.Id, saved2.Id);
        Assert.Equal(saved1.EventId, saved2.EventId);

        // Third call with same eventId + divergent payload => throws conflict
        var msgConflict = new CharacterOutboxMessage(
            sharedEventId, charId, "ActivityStarted", "{\"pg\":false}", "fp_divergent", now);

        await Assert.ThrowsAsync<CharacterOutboxIdempotencyConflictException>(async () =>
        {
            await repo1.AddOrGetAsync(msgConflict);
        });

        // Cleanup test row
        db.CharacterOutboxMessages.Remove(saved1);
        await db.SaveChangesAsync();
    }
}
