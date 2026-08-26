using Application.Common;
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

    [Fact]
    public async Task CrashConsistency_WhenWorkerCrashesAfterQualityPassBeforeAcceptance_RecoversWithoutDuplicateGpuInvocation()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new ProjectDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

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

        var compiler = new FakePromptCompiler("1man knight", "1girl");

        // 1. Simulate Worker 1 crashing after recording attempt Succeeded in DB, but before CAS Acceptance!
        using (var dbCrash = new ProjectDbContext(options))
        {
            var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, requestId);
            job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow.AddMinutes(-5)); // lease expired
            await dbCrash.ImageGenerationJobs.AddAsync(job);

            var fp = DeterministicSeedDerivation.ComputeFingerprint(
                job.Id, turnId, 1, 1, 100000L, snapshot.GenerationProfile.ParametersJson ?? string.Empty,
                "VisualIdentity", 1,
                compiler.CompileScenePrompt(snapshot), compiler.CompileNegativePrompt(snapshot), null);

            var attempt = new ImageGenerationAttempt(
                generationJobId: job.Id,
                turnId: turnId,
                sceneRevision: 1,
                attemptNumber: 1,
                derivedSeed: 100000L,
                parametersJson: snapshot.GenerationProfile.ParametersJson ?? string.Empty,
                generationFingerprint: fp,
                status: GenerationAttemptStatus.Running,
                claimedBy: "worker-1",
                startedAt: DateTime.UtcNow.AddMinutes(-5),
                leaseUntil: DateTime.UtcNow.AddMinutes(-3)
            );
            attempt.MarkSucceeded("https://cdn.project00.ai/images/attempt1_recovered.png", "comfy_job_1", 0.88f, 0.90f, DateTime.UtcNow.AddMinutes(-4));
            await dbCrash.ImageGenerationAttempts.AddAsync(attempt);
            await dbCrash.SaveChangesAsync();
        }

        // 2. Worker 2 restarts and executes generation
        var countingImageService = new ConcurrentTrackingImageService();
        var evaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var policy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MaxAttempts: 3);

        using var dbWorker2 = new ProjectDbContext(options);
        var orchestrator2 = new ImageGenerationOrchestrator(
            dbContext: dbWorker2,
            visualCompiler: compiler,
            imageService: countingImageService,
            logger: NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: evaluator,
            qualityGuardPolicy: policy
        );

        var result = await orchestrator2.OrchestrateSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-2", DateTime.UtcNow);

        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.Equal(0, countingImageService.CallCount); // ZERO duplicate GPU calls! Reused attempt ledger!

        // Assert exactly 1 SceneImage was promoted and isCurrent=true
        using var verifyDb = new ProjectDbContext(options);
        var images = await verifyDb.SceneImages.ToListAsync();
        Assert.Single(images);
        Assert.True(images[0].IsCurrent);
        Assert.Equal("https://cdn.project00.ai/images/attempt1_recovered.png", images[0].ImageUrl);

        var finalJob = await verifyDb.ImageGenerationJobs.FirstAsync();
        Assert.Equal(ImageJobStatus.Completed, finalJob.Status);
        Assert.NotNull(finalJob.AcceptedAttemptId);
    }

    [Fact]
    public async Task ArtifactAcceptance_WhenAttemptBelongsToDifferentJob_ThrowsInvalidOperationException()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var service = new ArtifactAcceptanceService(db, new SystemDateTimeProvider(), NullLogger<ArtifactAcceptanceService>.Instance);

        var jobA = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var jobB = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var attemptOfJobB = new ImageGenerationAttempt(jobB.Id, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_job_b");

        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: jobA.SessionId,
            CharacterId: jobA.CharacterId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 1000L)
        );

        var request = new ArtifactAcceptanceRequest(
            Job: jobA,
            WinningAttempt: attemptOfJobB, // Attempt belongs to Job B, but passed with Job A!
            Snapshot: snapshot,
            ImageUrl: "https://cdn.project00.ai/image.png",
            CompiledPrompt: "prompt",
            ResolvedPreviousSceneImageUrl: null,
            GenerationFingerprint: "fp_job_b",
            MetadataJson: null,
            IsIdentityPassed: true,
            WorkerId: "worker-1",
            OutboxId: Guid.NewGuid()
        );

        // P1-2 Invariant: MUST reject attempt belonging to another Job!
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptAttemptAtomicallyAsync(request));
    }

    [Fact]
    public async Task ArtifactAcceptance_TransactionalOutboxEvent_PersistedInSameTransaction()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new ProjectDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

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

        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var imageService = new ConcurrentTrackingImageService();
        var evaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var policy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MaxAttempts: 3);

        using var db = new ProjectDbContext(options);
        var orchestrator = new ImageGenerationOrchestrator(
            dbContext: db,
            visualCompiler: compiler,
            imageService: imageService,
            logger: NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: evaluator,
            qualityGuardPolicy: policy
        );

        var result = await orchestrator.OrchestrateSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow);
        Assert.Equal(JobExecutionStatus.Completed, result.Status);

        // Assert outbox message for GenerationJobAccepted was persisted atomically with the artifact!
        using var verifyDb = new ProjectDbContext(options);
        var outboxEvents = await verifyDb.OutboxMessages
            .Where(m => m.EventType == OutboxEventTypes.GenerationJobAccepted)
            .ToListAsync();
        var acceptedJob = await verifyDb.ImageGenerationJobs.FirstAsync();
        Assert.Single(outboxEvents);
        Assert.Contains(acceptedJob.Id.ToString(), outboxEvents[0].PayloadJson);
    }

    [Fact]
    public async Task CrashConsistency_WorkerCrashesAfterCommitBeforeResponse_ReplayOutboxReusesArtifactWithoutDuplicateGpu()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new ProjectDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

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

        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var trackingImageService = new ConcurrentTrackingImageService();
        var evaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var policy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MaxAttempts: 3);

        // 1. Worker 1 runs and fully commits the transaction
        using (var dbWorker1 = new ProjectDbContext(options))
        {
            var orchestrator1 = new ImageGenerationOrchestrator(
                dbContext: dbWorker1,
                visualCompiler: compiler,
                imageService: trackingImageService,
                logger: NullLogger<ImageGenerationOrchestrator>.Instance,
                dateTimeProvider: new SystemDateTimeProvider(),
                qualityEvaluator: evaluator,
                qualityGuardPolicy: policy
            );

            var res1 = await orchestrator1.OrchestrateSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow);
            Assert.Equal(JobExecutionStatus.Completed, res1.Status);
        }

        Assert.Equal(1, trackingImageService.CallCount);

        // 2. Outbox replays same message to Worker 2 (simulating crash right after commit before acking message)
        using (var dbWorker2 = new ProjectDbContext(options))
        {
            var orchestrator2 = new ImageGenerationOrchestrator(
                dbContext: dbWorker2,
                visualCompiler: compiler,
                imageService: trackingImageService,
                logger: NullLogger<ImageGenerationOrchestrator>.Instance,
                dateTimeProvider: new SystemDateTimeProvider(),
                qualityEvaluator: evaluator,
                qualityGuardPolicy: policy
            );

            var res2 = await orchestrator2.OrchestrateSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-2", DateTime.UtcNow);
            Assert.Equal(JobExecutionStatus.Skipped, res2.Status);
        }

        // Assert ZERO additional GPU calls!
        Assert.Equal(1, trackingImageService.CallCount);

        // Assert exactly 1 artifact in DB
        using (var verifyDb = new ProjectDbContext(options))
        {
            var images = await verifyDb.SceneImages.ToListAsync();
            Assert.Single(images);
            Assert.True(images[0].IsCurrent);
        }
    }
}
