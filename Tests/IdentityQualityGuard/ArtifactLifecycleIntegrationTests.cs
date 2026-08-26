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

public sealed class ArtifactLifecycleIntegrationTests
{
    [Fact]
    public async Task ImageGenerationJobHandler_WhenAttemptDegraded_RetriesAndRecoversOnAttempt2()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var imageService = new FakeImageService();

        // Attempt 1: Degraded (0.70 < 0.75) -> Triggers RetryAttenuated
        // Attempt 2: Recovered (0.86 >= 0.75) -> Succeeded & Accepted
        var evaluator = new SequenceEvaluator(new[]
        {
            IdentityEvaluationResult.Degrade(0.70f, 0.70f, 0.70f, Array.Empty<IdentityViolation>()),
            IdentityEvaluationResult.Pass(0.86f, 0.90f, 0.88f)
        });

        var policy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MaxAttempts: 3);

        var handler = new ImageGenerationJobHandler(
            dbContext: db,
            visualCompiler: compiler,
            imageService: imageService,
            logger: NullLogger<ImageGenerationJobHandler>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: evaluator,
            qualityGuardPolicy: policy
        );

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

        var result = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow);

        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.Equal(2, imageService.CallCount); // 2 GPU attempts executed
        Assert.Equal(2, evaluator.CallCount);

        var attempts = await db.ImageGenerationAttempts.OrderBy(a => a.AttemptNumber).ToListAsync();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(GenerationAttemptStatus.Degraded, attempts[0].Status);
        Assert.Equal(GenerationAttemptStatus.Succeeded, attempts[1].Status);

        var images = await db.SceneImages.ToListAsync();
        Assert.Single(images);
        Assert.True(images[0].IsCurrent);

        var job = await db.ImageGenerationJobs.FirstAsync();
        Assert.Equal(ImageJobStatus.Completed, job.Status);
        Assert.Equal(attempts[1].Id, job.AcceptedAttemptId);
    }

    [Fact]
    public async Task ImageGenerationJobHandler_WhenAllAttemptsExhausted_QuarantinesFrameFromContinuity()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var imageService = new FakeImageService();

        // 3 consecutive degraded attempts -> Exhaustion
        var evaluator = new SequenceEvaluator(new[]
        {
            IdentityEvaluationResult.Degrade(0.68f, 0.65f, 0.66f, Array.Empty<IdentityViolation>()),
            IdentityEvaluationResult.Degrade(0.69f, 0.66f, 0.67f, Array.Empty<IdentityViolation>()),
            IdentityEvaluationResult.Degrade(0.70f, 0.67f, 0.68f, Array.Empty<IdentityViolation>())
        });

        var policy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MaxAttempts: 3);

        var handler = new ImageGenerationJobHandler(
            dbContext: db,
            visualCompiler: compiler,
            imageService: imageService,
            logger: NullLogger<ImageGenerationJobHandler>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: evaluator,
            qualityGuardPolicy: policy
        );

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: 2,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 100000L),
            PreviousSceneImageUrl: "https://cdn.project00.ai/turn1_good.png"
        );

        var prevImage = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: Guid.NewGuid(),
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/turn1_good.png",
            prompt: "1man knight",
            isCurrent: true
        );
        await db.SceneImages.AddAsync(prevImage);
        await db.SaveChangesAsync();

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: characterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var result = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow);

        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.Equal(3, imageService.CallCount); // Exhausted all 3 attempts

        var attempts = await db.ImageGenerationAttempts.OrderBy(a => a.AttemptNumber).ToListAsync();
        Assert.Equal(3, attempts.Count);
        Assert.All(attempts, a => Assert.Equal(GenerationAttemptStatus.Degraded, a.Status));

        // P0 Quarantine Invariant: Frame is saved with IsCurrent = false, preserving Revision 1 continuity!
        var images = await db.SceneImages.OrderBy(i => i.SceneRevision).ToListAsync();
        Assert.Equal(2, images.Count);
        Assert.True(images[0].IsCurrent, "Predecessor revision 1 anchor must remain IsCurrent = true");
        Assert.False(images[1].IsCurrent, "Quarantined revision 2 frame must have IsCurrent = false");

        var job = await db.ImageGenerationJobs.FirstAsync();
        Assert.Equal(ImageJobStatus.Quarantined, job.Status);
    }

    [Fact]
    public async Task ImageGenerationJobHandler_CrashRecovery_ReusesDurableAttemptLedgerWithoutCallingGpu()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var imageService = new FakeImageService();
        var evaluator = new SequenceEvaluator(new[]
        {
            IdentityEvaluationResult.Pass(0.85f, 0.90f, 0.87f)
        });
        var policy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MaxAttempts: 3);

        var handler = new ImageGenerationJobHandler(
            dbContext: db,
            visualCompiler: compiler,
            imageService: imageService,
            logger: NullLogger<ImageGenerationJobHandler>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: evaluator,
            qualityGuardPolicy: policy
        );

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

        // Step 1: Worker 1 crashes right after attempt succeeded before SceneImage commit
        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, requestId);
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var fp = DeterministicSeedDerivation.ComputeFingerprint(
            job.Id, turnId, 1, 1, 100000L, snapshot.GenerationProfile.ParametersJson ?? string.Empty,
            "VisualIdentity", 1,
            compiler.CompileScenePrompt(snapshot), compiler.CompileNegativePrompt(snapshot), null);

        var crashedAttempt = new ImageGenerationAttempt(
            generationJobId: job.Id,
            turnId: turnId,
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 100000L,
            parametersJson: snapshot.GenerationProfile.ParametersJson ?? string.Empty,
            generationFingerprint: fp,
            status: GenerationAttemptStatus.Running,
            claimedBy: "worker-1",
            startedAt: DateTime.UtcNow.AddMinutes(-1),
            leaseUntil: DateTime.UtcNow.AddMinutes(1)
        );
        crashedAttempt.MarkSucceeded("https://cdn.project00.ai/images/recovered_image.png", "job_recovered", 0.85f, 0.90f, DateTime.UtcNow);
        await db.ImageGenerationAttempts.AddAsync(crashedAttempt);
        await db.SaveChangesAsync();

        // Step 2: Worker 2 restarts and picks up the job. Must reuse the durable attempt ledger without calling GPU!
        var result2 = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-2", DateTime.UtcNow);
        Assert.Equal(JobExecutionStatus.Completed, result2.Status);
        Assert.Equal(0, imageService.CallCount); // 0 GPU calls because it reused the succeeded attempt!

        var sceneImages1 = await db.SceneImages.ToListAsync();
        Assert.Single(sceneImages1);
        Assert.Equal("https://cdn.project00.ai/images/recovered_image.png", sceneImages1[0].ImageUrl);
        Assert.Equal(fp, sceneImages1[0].GenerationFingerprint);

        var attempts1 = await db.ImageGenerationAttempts.ToListAsync();
        Assert.Single(attempts1);
        Assert.Equal(GenerationAttemptStatus.Succeeded, attempts1[0].Status);

        // Step 3: Outbox replay / redundant delivery of the same RequestId -> returns Skipped with 0 GPU calls & 0 duplicate DB rows
        var result3 = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-3", DateTime.UtcNow);
        Assert.Equal(JobExecutionStatus.Skipped, result3.Status);
        Assert.Equal(0, imageService.CallCount);

        var sceneImages2 = await db.SceneImages.ToListAsync();
        Assert.Single(sceneImages2);
        Assert.Equal(sceneImages1[0].Id, sceneImages2[0].Id);

        var attempts2 = await db.ImageGenerationAttempts.ToListAsync();
        Assert.Single(attempts2);
    }

    [Fact]
    public async Task ImageGenerationAttempt_RelationalUniqueConstraint_ThrowsDbUpdateException_OnDuplicateFingerprint()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new ProjectDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var turnId = Guid.NewGuid();
            var fp = "duplicate_fingerprint_hash_12345";

            var job = new ImageGenerationJob(Guid.NewGuid(), turnId, Guid.NewGuid(), 1);
            await db.ImageGenerationJobs.AddAsync(job);
            await db.SaveChangesAsync();

            var attempt1 = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", fp);
            await db.ImageGenerationAttempts.AddAsync(attempt1);
            await db.SaveChangesAsync();

            var attempt2 = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", fp);
            await db.ImageGenerationAttempts.AddAsync(attempt2);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("UNIQUE", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ConcurrentExpiredLeaseClaim_AllowsExactlyOneGpuExecution()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new ProjectDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();

            var compilerInit = new FakePromptCompiler("1man knight", "1girl");
            var sessionIdInit = Guid.NewGuid();
            var turnIdInit = Guid.NewGuid();
            var requestIdInit = Guid.NewGuid();
            var characterIdInit = Guid.NewGuid();

            var snapshotInit = new VisualSnapshot(
                TurnId: turnIdInit,
                SessionId: sessionIdInit,
                CharacterId: characterIdInit,
                SceneRevision: 1,
                VisualIdentity: null,
                SceneState: new SessionSceneState("courtyard", "standing"),
                TransientState: null,
                GenerationProfile: GenerationProfile.CreateDefault(seed: 100000L)
            );

            // Seed expired job in DB (expired 8 minutes ago)
            var now = DateTime.UtcNow;
            var expiredTime = now.AddMinutes(-10);
            var jobInit = new ImageGenerationJob(sessionIdInit, turnIdInit, characterIdInit, 1, requestIdInit);
            jobInit.TryClaim("old-crashed-worker", TimeSpan.FromMinutes(2), expiredTime);
            await dbInit.ImageGenerationJobs.AddAsync(jobInit);

            // Compute attempt 1 fingerprint
            var fp = DeterministicSeedDerivation.ComputeFingerprint(
                jobInit.Id, turnIdInit, 1, 1, 100000L, snapshotInit.GenerationProfile.ParametersJson ?? string.Empty,
                "VisualIdentity", 1,
                compilerInit.CompileScenePrompt(snapshotInit), compilerInit.CompileNegativePrompt(snapshotInit), null);

            // Seed expired attempt in DB
            var attemptInit = new ImageGenerationAttempt(
                generationJobId: jobInit.Id,
                turnId: turnIdInit,
                sceneRevision: 1,
                attemptNumber: 1,
                derivedSeed: 100000L,
                parametersJson: snapshotInit.GenerationProfile.ParametersJson ?? string.Empty,
                generationFingerprint: fp,
                status: GenerationAttemptStatus.Running,
                claimedBy: "old-crashed-worker",
                startedAt: expiredTime,
                leaseUntil: expiredTime.AddMinutes(2)
            );
            await dbInit.ImageGenerationAttempts.AddAsync(attemptInit);
            await dbInit.SaveChangesAsync();
        }

        var trackingImageService = new ConcurrentTrackingImageService();
        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var evaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var policy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MaxAttempts: 3);

        using var dbQuery = new ProjectDbContext(options);
        var existingJob = await dbQuery.ImageGenerationJobs.FirstAsync();
        var existingSnapshot = new VisualSnapshot(
            TurnId: existingJob.TurnId,
            SessionId: existingJob.SessionId,
            CharacterId: existingJob.CharacterId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 100000L)
        );

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: existingJob.TurnId,
            CharacterId: existingJob.CharacterId,
            UserId: Guid.NewGuid(),
            Snapshot: existingSnapshot,
            GenerationRequestId: existingJob.GenerationRequestId
        );

        using var db1 = new ProjectDbContext(options);
        using var db2 = new ProjectDbContext(options);

        var handler1 = new ImageGenerationJobHandler(
            dbContext: db1,
            visualCompiler: compiler,
            imageService: trackingImageService,
            logger: NullLogger<ImageGenerationJobHandler>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: evaluator,
            qualityGuardPolicy: policy
        );

        var handler2 = new ImageGenerationJobHandler(
            dbContext: db2,
            visualCompiler: compiler,
            imageService: trackingImageService,
            logger: NullLogger<ImageGenerationJobHandler>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: evaluator,
            qualityGuardPolicy: policy
        );

        var raceTime = DateTime.UtcNow;

        // Two workers race to claim the expired attempt concurrently!
        var task1 = Task.Run(() => handler1.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", raceTime));
        var task2 = Task.Run(() => handler2.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-2", raceTime));

        var results = await Task.WhenAll(task1, task2);

        // Assert exactly ONE GPU invocation happened across both workers
        Assert.Equal(1, trackingImageService.CallCount);

        // Assert at least one worker completed successfully
        Assert.Contains(results, r => r.Status == JobExecutionStatus.Completed);

        // Assert exactly 1 SceneImage artifact exists in DB
        using var verifyDb = new ProjectDbContext(options);
        var artifacts = await verifyDb.SceneImages.ToListAsync();
        Assert.Single(artifacts);
        Assert.True(artifacts[0].IsCurrent);

        // Assert exactly 1 ImageGenerationAttempt exists in DB and is marked Succeeded
        var attempts = await verifyDb.ImageGenerationAttempts.ToListAsync();
        Assert.Single(attempts);
        Assert.Equal(GenerationAttemptStatus.Succeeded, attempts[0].Status);
    }

    [Fact]
    public async Task SceneImage_WithDuplicateFingerprint_ThrowsDbUpdateExceptionOnRelationalDatabase()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new ProjectDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var sessionId = Guid.NewGuid();
            var characterId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            var fp = "duplicate_scene_image_fp_999";

            var img1 = new SceneImage(sessionId, characterId, turnId, 1, "https://cdn.project00.ai/img1.png", "prompt", generationFingerprint: fp);
            await db.SceneImages.AddAsync(img1);
            await db.SaveChangesAsync();

            var img2 = new SceneImage(sessionId, characterId, turnId, 1, "https://cdn.project00.ai/img2.png", "prompt", generationFingerprint: fp);
            await db.SceneImages.AddAsync(img2);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("UNIQUE", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ImageGenerationJobHandler_WhenAttemptIsRunningUnderActiveLeaseByAnotherWorker_DefersExecutionWithoutGpuCall()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var imageService = new FakeImageService();
        var evaluator = new SequenceEvaluator(new[] { IdentityEvaluationResult.Pass(0.85f, 0.90f, 0.87f) });
        var policy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MaxAttempts: 3);

        var handler = new ImageGenerationJobHandler(
            dbContext: db,
            visualCompiler: compiler,
            imageService: imageService,
            logger: NullLogger<ImageGenerationJobHandler>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: evaluator,
            qualityGuardPolicy: policy
        );

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

        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, requestId);
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var fp = DeterministicSeedDerivation.ComputeFingerprint(
            job.Id, turnId, 1, 1, 100000L, snapshot.GenerationProfile.ParametersJson ?? string.Empty,
            "VisualIdentity", 1,
            compiler.CompileScenePrompt(snapshot), compiler.CompileNegativePrompt(snapshot), null);

        var activeAttempt = new ImageGenerationAttempt(
            generationJobId: job.Id,
            turnId: turnId,
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 100000L,
            parametersJson: snapshot.GenerationProfile.ParametersJson ?? string.Empty,
            generationFingerprint: fp,
            status: GenerationAttemptStatus.Running,
            claimedBy: "worker-A",
            startedAt: DateTime.UtcNow,
            leaseUntil: DateTime.UtcNow.AddMinutes(2)
        );
        await db.ImageGenerationAttempts.AddAsync(activeAttempt);
        await db.SaveChangesAsync();

        var result = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-B", DateTime.UtcNow);

        Assert.Equal(JobExecutionStatus.Deferred, result.Status);
        Assert.Equal(0, imageService.CallCount); // 0 GPU calls because worker-A is actively generating!
    }

    [Fact]
    public void ImageGenerationJobHandler_Constructor_WhenEvaluatorNull_ThrowsArgumentNullException()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ProjectDbContext(options);

        Assert.Throws<ArgumentNullException>(() => new ImageGenerationJobHandler(
            dbContext: db,
            visualCompiler: new FakePromptCompiler("pos", "neg"),
            imageService: new FakeImageService(),
            logger: NullLogger<ImageGenerationJobHandler>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: null!
        ));
    }

    [Fact]
    public async Task PredecessorLineageResolver_OnlyResolvesFromAcceptedCurrentArtifacts()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var resolver = new PredecessorLineageResolver(db, NullLogger<PredecessorLineageResolver>.Instance);

        var sessionId = Guid.NewGuid();

        // 1. Revision 1: Resolves fallback or null without predecessor check
        var (ready1, url1, reason1) = await resolver.ResolvePredecessorReferenceAsync(sessionId, 1, null, "https://cdn.project00.ai/avatar.png");
        Assert.True(ready1);
        Assert.Equal("https://cdn.project00.ai/avatar.png", url1);
        Assert.Null(reason1);

        // 2. Revision 2 without Revision 1 artifact in DB -> returns IsReady = false (Deferred)
        var (ready2, url2, reason2) = await resolver.ResolvePredecessorReferenceAsync(sessionId, 2, null, null);
        Assert.False(ready2);
        Assert.Null(url2);
        Assert.Contains("not yet completed", reason2);

        // 3. Add non-current (quarantined) Revision 1 image -> Still not ready!
        var nonCurrentImage = new SceneImage(sessionId, Guid.NewGuid(), Guid.NewGuid(), 1, "https://cdn.project00.ai/rev1_quarantined.png", "prompt", isCurrent: false);
        await db.SceneImages.AddAsync(nonCurrentImage);
        await db.SaveChangesAsync();

        var (ready3, url3, reason3) = await resolver.ResolvePredecessorReferenceAsync(sessionId, 2, null, null);
        Assert.False(ready3, "Non-current / quarantined artifact cannot serve as predecessor reference");

        // 4. Promote/Add current accepted Revision 1 image -> IsReady = true with authoritative URL!
        var currentImage = new SceneImage(sessionId, Guid.NewGuid(), Guid.NewGuid(), 1, "https://cdn.project00.ai/rev1_accepted.png", "prompt", isCurrent: true);
        await db.SceneImages.AddAsync(currentImage);
        await db.SaveChangesAsync();

        var (ready4, url4, reason4) = await resolver.ResolvePredecessorReferenceAsync(sessionId, 2, null, null);
        Assert.True(ready4);
        Assert.Equal("https://cdn.project00.ai/rev1_accepted.png", url4);
        Assert.Null(reason4);
    }
}
