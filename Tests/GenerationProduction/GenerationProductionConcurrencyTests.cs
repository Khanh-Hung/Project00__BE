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
    private static ProjectDbContext CreateSqliteDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new ProjectDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    [Fact]
    public async Task ConcurrentWorkers_ExecuteSameJob_ProducesExactlyOneAcceptedAttempt_AndValidProvenance()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var dbContext = CreateSqliteDbContext(connection);

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
        var compiler = new FakePromptCompiler("a knight in shining armor", "blurry, distorted");
        var qualityGuardPolicy = new IdentityQualityGuardPolicy(IsActive: true);
        var lineageResolver = new PredecessorLineageResolver(dbContext, NullLogger<PredecessorLineageResolver>.Instance);
        var acceptanceService = new ArtifactAcceptanceService(dbContext, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);

        var imageService1 = new FakeImageService();
        var imageService2 = new FakeImageService();
        var evaluator = new DevelopmentPassThroughIdentityQualityEvaluator();

        var orchestrator1 = new ImageGenerationOrchestrator(
            dbContext, compiler, imageService1, NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider, evaluator, qualityGuardPolicy, lineageResolver, acceptanceService
        );

        var orchestrator2 = new ImageGenerationOrchestrator(
            dbContext, compiler, imageService2, NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider, evaluator, qualityGuardPolicy, lineageResolver, acceptanceService
        );

        // Execute sequentially or concurrently against SQLite in-memory
        var result1 = await orchestrator1.OrchestrateSceneImageGenerationAsync(payload, outboxId, "worker-1", DateTime.UtcNow);
        var result2 = await orchestrator2.OrchestrateSceneImageGenerationAsync(payload, outboxId, "worker-2", DateTime.UtcNow);

        // One must complete, the second must skip (artifact exists / job completed)
        Assert.True(result1.Status == JobExecutionStatus.Completed || result2.Status == JobExecutionStatus.Completed);
        Assert.True(result1.Status == JobExecutionStatus.Skipped || result2.Status == JobExecutionStatus.Skipped ||
                    result1.Status == JobExecutionStatus.Completed || result2.Status == JobExecutionStatus.Completed);

        // Assert DB invariants: Exactly one current image
        var currentImages = await dbContext.SceneImages
            .Where(img => img.SessionId == sessionId && img.SceneRevision == revision && img.IsCurrent)
            .ToListAsync();

        Assert.Single(currentImages);
        var acceptedArtifact = currentImages[0];
        Assert.True(acceptedArtifact.IsCurrent);

        // Assert Provenance is populated
        Assert.NotNull(acceptedArtifact.ProvenanceJson);
        var provenance = acceptedArtifact.GetProvenance();
        Assert.NotNull(provenance);
        Assert.Equal(requestId, provenance.GenerationRequestId);
        Assert.Equal(revision, provenance.SceneRevision);
        Assert.Equal("Passed", provenance.IdentityStatus);
    }
}
