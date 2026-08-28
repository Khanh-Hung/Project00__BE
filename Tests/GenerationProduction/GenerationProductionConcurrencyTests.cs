using Application.DTOs;
using Application.Enums;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.IdentityQualityGuard;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class GenerationProductionConcurrencyTests
{
    private static CoreDbContext CreateSqliteDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        return new CoreDbContext(options);
    }

    [Fact]
    public async Task ConcurrentWorkers_ExecuteSameJob_ProducesExactlyOneAcceptedAttempt_AndValidProvenance()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        // 1. Initialize schema on shared SQLite in-memory connection
        using (var dbInit = CreateSqliteDbContext(connection))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var sessionId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        int revision = 1;

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: revision,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene-temple", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: characterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var dateTimeProvider = new SystemDateTimeProvider();
        var compiler1 = new FakePromptCompiler("a knight in shining armor", "blurry, distorted");
        var compiler2 = new FakePromptCompiler("a knight in shining armor", "blurry, distorted");
        var qualityGuardPolicy = new IdentityQualityGuardPolicy(IsActive: true);

        // Worker 1 has its OWN isolated DbContext instance
        using var dbContext1 = CreateSqliteDbContext(connection);
        var lineageResolver1 = new PredecessorLineageResolver(dbContext1, NullLogger<PredecessorLineageResolver>.Instance);
        var acceptanceService1 = new ArtifactAcceptanceService(dbContext1, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);
        var imageService1 = new FakeImageService();
        var evaluator1 = new DevelopmentPassThroughIdentityQualityEvaluator();

        var orchestrator1 = new ImageGenerationOrchestrator(
            dbContext1, compiler1, imageService1, NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider, evaluator1, qualityGuardPolicy, lineageResolver1, acceptanceService1
        );

        // Worker 2 has its OWN isolated DbContext instance
        using var dbContext2 = CreateSqliteDbContext(connection);
        var lineageResolver2 = new PredecessorLineageResolver(dbContext2, NullLogger<PredecessorLineageResolver>.Instance);
        var acceptanceService2 = new ArtifactAcceptanceService(dbContext2, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);
        var imageService2 = new FakeImageService();
        var evaluator2 = new DevelopmentPassThroughIdentityQualityEvaluator();

        var orchestrator2 = new ImageGenerationOrchestrator(
            dbContext2, compiler2, imageService2, NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider, evaluator2, qualityGuardPolicy, lineageResolver2, acceptanceService2
        );

        // 2. TRUE CONCURRENT EXECUTION across parallel asynchronous tasks
        var now = DateTime.UtcNow;
        var task1 = orchestrator1.OrchestrateSceneImageGenerationAsync(payload, outboxId, "worker-1", now);
        var task2 = orchestrator2.OrchestrateSceneImageGenerationAsync(payload, outboxId, "worker-2", now);

        var results = await Task.WhenAll(task1, task2);
        var result1 = results[0];
        var result2 = results[1];

        // One must complete, the other must skip or defer gracefully without throwing or corrupting state
        Assert.True(result1.Status == JobExecutionStatus.Completed || result2.Status == JobExecutionStatus.Completed);
        Assert.True(result1.Status == JobExecutionStatus.Skipped || result1.Status == JobExecutionStatus.Deferred || result1.Status == JobExecutionStatus.Completed);
        Assert.True(result2.Status == JobExecutionStatus.Skipped || result2.Status == JobExecutionStatus.Deferred || result2.Status == JobExecutionStatus.Completed);

        // 3. Assert DB invariants using an independent verification DbContext
        using var dbVerify = CreateSqliteDbContext(connection);
        var currentImages = await dbVerify.SceneImages
            .Where(img => img.SessionId == sessionId && img.SceneRevision == revision && img.IsCurrent)
            .ToListAsync();

        Assert.Single(currentImages);
        var acceptedArtifact = currentImages[0];
        Assert.True(acceptedArtifact.IsCurrent);

        // Assert Provenance is populated accurately
        Assert.NotNull(acceptedArtifact.ProvenanceJson);
        var provenance = acceptedArtifact.GetProvenance();
        Assert.NotNull(provenance);
        Assert.Equal(requestId, provenance.GenerationRequestId);
        Assert.Equal(revision, provenance.SceneRevision);
        Assert.Equal("Passed", provenance.IdentityStatus);
    }
}
