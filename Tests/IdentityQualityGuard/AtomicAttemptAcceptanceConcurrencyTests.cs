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
using Xunit;

namespace Tests.IdentityQualityGuard;

public sealed class AtomicAttemptAcceptanceConcurrencyTests
{
    [Fact]
    public async Task ConcurrentAttemptAcceptance_AllowsExactlyOneWorkerToAcceptAndPromoteArtifact()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 100000L)
        );

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: characterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        using (var dbInit = new ProjectDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var trackingImageService = new ConcurrentTrackingImageService();
        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var evaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var policy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MaxAttempts: 3);

        using var db1 = new ProjectDbContext(options);
        using var db2 = new ProjectDbContext(options);

        var orchestrator1 = new ImageGenerationOrchestrator(
            dbContext: db1,
            visualCompiler: compiler,
            imageService: trackingImageService,
            logger: NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: evaluator,
            qualityGuardPolicy: policy
        );

        var orchestrator2 = new ImageGenerationOrchestrator(
            dbContext: db2,
            visualCompiler: compiler,
            imageService: trackingImageService,
            logger: NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: evaluator,
            qualityGuardPolicy: policy
        );

        var raceTime = DateTime.UtcNow;

        // Two workers race to execute generation and accept the attempt concurrently!
        var task1 = Task.Run(() => orchestrator1.OrchestrateSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", raceTime));
        var task2 = Task.Run(() => orchestrator2.OrchestrateSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-2", raceTime));

        var results = await Task.WhenAll(task1, task2);

        // Assert at least one worker completed successfully
        Assert.Contains(results, r => r.Status == JobExecutionStatus.Completed);

        // Assert exactly 1 SceneImage artifact exists in DB and is marked Current
        using var verifyDb = new ProjectDbContext(options);
        var artifacts = await verifyDb.SceneImages.ToListAsync();
        Assert.Single(artifacts);
        Assert.True(artifacts[0].IsCurrent);

        // Assert the ImageGenerationJob in DB has AcceptedAttemptId populated and is marked Completed
        var job = await verifyDb.ImageGenerationJobs.FirstAsync();
        Assert.Equal(ImageJobStatus.Completed, job.Status);
        Assert.NotNull(job.AcceptedAttemptId);
        Assert.NotEqual(Guid.Empty, job.AcceptedAttemptId.Value);

        // Assert the winning attempt matches AcceptedAttemptId
        var acceptedAttempt = await verifyDb.ImageGenerationAttempts.FirstOrDefaultAsync(a => a.Id == job.AcceptedAttemptId.Value);
        Assert.NotNull(acceptedAttempt);
        Assert.Equal(GenerationAttemptStatus.Succeeded, acceptedAttempt.Status);
    }
}
