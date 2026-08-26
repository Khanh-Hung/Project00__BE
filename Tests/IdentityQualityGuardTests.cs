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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests;

public sealed class IdentityQualityGuardTests
{
    [Fact]
    public void DeterministicSeedDerivation_Attempt1_ReturnsBaseSeedUnmodified()
    {
        long baseSeed = 42424242L;
        long derived = DeterministicSeedDerivation.Derive(baseSeed, attemptNumber: 1);
        Assert.Equal(baseSeed, derived);
    }

    [Fact]
    public void DeterministicSeedDerivation_HigherAttempts_ProduceDeterministicDistinctSeeds()
    {
        long baseSeed = 100000L;
        long seed1 = DeterministicSeedDerivation.Derive(baseSeed, 1);
        long seed2 = DeterministicSeedDerivation.Derive(baseSeed, 2);
        long seed3 = DeterministicSeedDerivation.Derive(baseSeed, 3);

        Assert.Equal(baseSeed, seed1);
        Assert.NotEqual(seed1, seed2);
        Assert.NotEqual(seed2, seed3);
        Assert.NotEqual(seed1, seed3);

        // Determinism test: calling again returns exact same seeds
        Assert.Equal(seed2, DeterministicSeedDerivation.Derive(baseSeed, 2));
        Assert.Equal(seed3, DeterministicSeedDerivation.Derive(baseSeed, 3));
    }

    [Fact]
    public void DeterministicSeedDerivation_DifferentBaseSeeds_ProduceDifferentDerivations()
    {
        long seedA = DeterministicSeedDerivation.Derive(100L, 2);
        long seedB = DeterministicSeedDerivation.Derive(200L, 2);
        Assert.NotEqual(seedA, seedB);
    }

    [Fact]
    public void DeterministicSeedDerivation_Fingerprint_IsDeterministicAndSensitiveToParameters()
    {
        var jobId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var fp1 = DeterministicSeedDerivation.ComputeFingerprint(jobId, snapshotId, 1, 1, 12345L, "{\"weight\":0.12}");
        var fp2 = DeterministicSeedDerivation.ComputeFingerprint(jobId, snapshotId, 1, 1, 12345L, "{\"weight\":0.12}");
        var fpDifferentAttempt = DeterministicSeedDerivation.ComputeFingerprint(jobId, snapshotId, 1, 2, 99999L, "{\"weight\":0.06}");

        Assert.Equal(fp1, fp2);
        Assert.NotEqual(fp1, fpDifferentAttempt);
    }

    [Theory]
    [InlineData(0.80f, 0.85f, false, IdentityStatus.Passed)]
    [InlineData(0.65f, 0.85f, false, IdentityStatus.Degraded)] // Identity below 0.75
    [InlineData(0.80f, 0.40f, false, IdentityStatus.Degraded)] // Feature below 0.50
    [InlineData(0.80f, 0.85f, true, IdentityStatus.Failed)]    // Hard Invariant violation
    public void IdentityQualityGuardPolicy_EvaluateStatus_CategorizesStatusAccurately(
        float identitySim, float featScore, bool invariantViolated, IdentityStatus expectedStatus)
    {
        var policy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MinAcceptableFeatureScore: 0.50f);
        var status = policy.EvaluateStatus(identitySim, featScore, invariantViolated, out var violations);

        Assert.Equal(expectedStatus, status);
        if (expectedStatus == IdentityStatus.Passed)
        {
            Assert.Empty(violations);
        }
        else
        {
            Assert.NotEmpty(violations);
        }
    }

    [Fact]
    public void IdentityQualityGuardPolicy_DecideMitigation_EscalatesDeterministically()
    {
        var policy = new IdentityQualityGuardPolicy(MaxAttempts: 3);

        var passEval = IdentityEvaluationResult.Pass(0.82f, 0.90f, 0.86f);
        Assert.Equal(QualityMitigationAction.Pass, policy.DecideMitigation(1, passEval));

        var degradeEval = IdentityEvaluationResult.Degrade(0.68f, 0.80f, 0.74f, new[] {
            new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "FACE_SIMILARITY_DEGRADED", "Face drift", false)
        });
        // Attempt 1 with minor degradation -> RetryAttenuated
        Assert.Equal(QualityMitigationAction.RetryAttenuated, policy.DecideMitigation(1, degradeEval));

        // Attempt 2 with degradation -> RetryIsolated
        Assert.Equal(QualityMitigationAction.RetryIsolated, policy.DecideMitigation(2, degradeEval));

        // Attempt 3 with degradation -> RejectDegraded (Max attempts reached)
        Assert.Equal(QualityMitigationAction.RejectDegraded, policy.DecideMitigation(3, degradeEval));

        var failedEval = IdentityEvaluationResult.Fail(0.50f, 0.30f, 0.40f, new[] {
            new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "INVARIANT_VIOLATION", "Hard violation", true)
        });
        // Attempt 1 with severe invariant failure -> immediately RetryIsolated
        Assert.Equal(QualityMitigationAction.RetryIsolated, policy.DecideMitigation(1, failedEval));
    }

    [Theory]
    [InlineData("AiProviders:ImageGeneration:QualityGuard:MinFaceSimilarity", "1.5")]
    [InlineData("AiProviders:ImageGeneration:QualityGuard:MinFaceSimilarity", "-0.1")]
    [InlineData("AiProviders:ImageGeneration:QualityGuard:MaxAttempts", "0")]
    [InlineData("AiProviders:ImageGeneration:QualityGuard:MaxAttempts", "99")]
    public void IdentityQualityGuardPolicy_FromConfiguration_ThrowsOnInvalidConfig(string key, string invalidVal)
    {
        var config = new ConfigurationManager();
        config[key] = invalidVal;

        Assert.Throws<InvalidOperationException>(() => IdentityQualityGuardPolicy.FromConfiguration(config));
    }

    [Fact]
    public void IdentityMitigationProfileResolver_ResolvesAdjustedProfilesProperly()
    {
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 2,
            VisualIdentity: null,
            SceneState: new SessionSceneState("throne room", "sitting"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 12345L, parametersJson: "{\"ipAdapter\":{\"weight\":0.60,\"endAt\":0.85},\"sceneContinuity\":{\"weight\":0.12,\"endAt\":0.25,\"weightType\":\"style transfer\"}}")
        );

        // 1. Pass returns unmodified
        var (passProfile, passSeed) = IdentityMitigationProfileResolver.ResolveMitigation(
            snapshot, QualityMitigationAction.Pass, 1, 12345L);
        Assert.Equal(12345L, passSeed);
        Assert.Equal(snapshot.GenerationProfile.ParametersJson, passProfile.ParametersJson);

        // 2. RetryAttenuated attenuates Slot 2
        var (attProfile, attSeed) = IdentityMitigationProfileResolver.ResolveMitigation(
            snapshot, QualityMitigationAction.RetryAttenuated, 2, 12345L);
        Assert.NotEqual(12345L, attSeed);
        Assert.Contains("\"weight\":0.06", attProfile.ParametersJson!);

        // 3. RetryIsolated zeroes Slot 2
        var (isoProfile, isoSeed) = IdentityMitigationProfileResolver.ResolveMitigation(
            snapshot, QualityMitigationAction.RetryIsolated, 3, 12345L);
        Assert.NotEqual(12345L, isoSeed);
        Assert.Contains("\"weight\":0,", isoProfile.ParametersJson!);
    }

    [Fact]
    public async Task ImageGenerationJobHandler_WhenAttempt1Fails_RetriesAndPromotesPassedAttempt2()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var imageService = new FakeImageService();
        var evaluator = new SequenceEvaluator(new[]
        {
            IdentityEvaluationResult.Degrade(0.60f, 0.45f, 0.52f, new[] {
                new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "FACE_SIMILARITY_DEGRADED", "Face drift", false)
            }),
            IdentityEvaluationResult.Pass(0.85f, 0.90f, 0.88f)
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

        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 100000L)
        );

        var payload = new SceneImageGenerationOutboxPayload(snapshot.TurnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, Guid.NewGuid());
        var result = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow);

        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.Equal(2, imageService.CallCount);
        Assert.Equal(2, evaluator.CallCount);

        var savedImage = await db.SceneImages.FirstOrDefaultAsync();
        Assert.NotNull(savedImage);
        Assert.Equal("https://cdn.project00.ai/images/gen_attempt_2.png", savedImage.ImageUrl);
        Assert.True(savedImage.IsCurrent, "Successfully recovered attempt 2 must be marked as IsCurrent = true");
        Assert.NotNull(savedImage.GenerationFingerprint);
        Assert.NotEmpty(savedImage.GenerationFingerprint);
    }

    [Fact]
    public async Task ImageGenerationJobHandler_WhenAllAttemptsFail_QuarantinesArtifactAndDoesNotPromoteToCurrent()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var sessionId = Guid.NewGuid();
        // Seed a prior successful scene image
        var priorSuccess = new SceneImage(
            sessionId: sessionId,
            characterId: Guid.NewGuid(),
            turnId: Guid.NewGuid(),
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/images/prior_success.png",
            prompt: "1man knight",
            isCurrent: true
        );
        await db.SceneImages.AddAsync(priorSuccess);
        await db.SaveChangesAsync();

        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var imageService = new FakeImageService();
        var evaluator = new SequenceEvaluator(new[]
        {
            IdentityEvaluationResult.Degrade(0.50f, 0.40f, 0.45f, new[] {
                new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "FACE_SIMILARITY_DEGRADED", "Severe drift", false)
            }),
            IdentityEvaluationResult.Degrade(0.52f, 0.42f, 0.47f, new[] {
                new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "FACE_SIMILARITY_DEGRADED", "Severe drift", false)
            }),
            IdentityEvaluationResult.Degrade(0.51f, 0.41f, 0.46f, new[] {
                new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "FACE_SIMILARITY_DEGRADED", "Severe drift", false)
            })
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

        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: sessionId,
            CharacterId: Guid.NewGuid(),
            SceneRevision: 2,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 100000L)
        );

        var payload = new SceneImageGenerationOutboxPayload(snapshot.TurnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, Guid.NewGuid());
        var result = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow);

        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.Equal(3, imageService.CallCount); // Bounded at 3 attempts

        // Verify that the failed attempt was saved as a quarantined diagnostic artifact (IsCurrent = false)
        var quarantinedImage = await db.SceneImages.FirstOrDefaultAsync(img => img.SceneRevision == 2);
        Assert.NotNull(quarantinedImage);
        Assert.False(quarantinedImage.IsCurrent, "Quarantined failed artifact must NOT have IsCurrent = true");

        // Verify that the prior successful image is STILL the current image!
        var currentImage = await db.SceneImages.FirstOrDefaultAsync(img => img.SessionId == sessionId && img.IsCurrent);
        Assert.NotNull(currentImage);
        Assert.Equal(priorSuccess.Id, currentImage.Id);
        Assert.Equal("https://cdn.project00.ai/images/prior_success.png", currentImage.ImageUrl);
    }

    [Fact]
    public void IdentityMitigationProfileResolver_WhenJsonMalformed_ThrowsInvalidOperationException()
    {
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 100L, parametersJson: "{malformed_json: true, missing_quotes}")
        );

        Assert.Throws<InvalidOperationException>(() =>
            IdentityMitigationProfileResolver.ResolveMitigation(snapshot, QualityMitigationAction.RetryAttenuated, 2, 100L));
    }

    [Fact]
    public async Task ImageGenerationJobHandler_WhenAttemptAlreadyGenerated_ReusesArtifactIdempotently()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var compiler = new FakePromptCompiler("1man knight in armor", "1girl");
        var imageService = new FakeImageService();
        var evaluator = new SequenceEvaluator(new[] { IdentityEvaluationResult.Pass(0.85f, 0.90f, 0.88f) });
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

        // Pre-seed an existing artifact in DB representing a previous run of this attempt
        var preExisting = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/images/reused_attempt.png",
            prompt: "1man knight in armor",
            generationRequestId: requestId,
            generationJobId: Guid.NewGuid(),
            isCurrent: true
        );
        await db.SceneImages.AddAsync(preExisting);
        await db.SaveChangesAsync();

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: characterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var result = await handler.HandleSceneImageGenerationAsync(
            payload: payload,
            outboxId: Guid.NewGuid(),
            workerId: "worker-1",
            now: DateTime.UtcNow
        );

        // Execution is skipped or reuses artifact without calling external GPU generator!
        Assert.Equal(JobExecutionStatus.Skipped, result.Status);
        Assert.Equal(0, imageService.CallCount);
    }

    [Fact]
    public async Task ImageGenerationAttempt_WithDuplicateFingerprint_ThrowsDbUpdateExceptionOnRelationalDatabase()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var fp = "test_fingerprint_unique_abc_123";
        var attempt1 = new ImageGenerationAttempt(
            generationJobId: job.Id,
            turnId: Guid.NewGuid(),
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 100L,
            parametersJson: "{}",
            generationFingerprint: fp,
            status: GenerationAttemptStatus.Running
        );
        await db.ImageGenerationAttempts.AddAsync(attempt1);
        await db.SaveChangesAsync();

        var attempt2 = new ImageGenerationAttempt(
            generationJobId: job.Id,
            turnId: Guid.NewGuid(),
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 100L,
            parametersJson: "{}",
            generationFingerprint: fp,
            status: GenerationAttemptStatus.Running
        );

        await db.ImageGenerationAttempts.AddAsync(attempt2);
        
        // Strictly asserts database rejects duplicate fingerprint with DbUpdateException
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SceneImage_WithDuplicateFingerprint_ThrowsDbUpdateExceptionOnRelationalDatabase()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var fp = "test_scene_image_fp_unique_xyz_789";
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var img1 = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: Guid.NewGuid(),
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/images/1.png",
            prompt: "1man knight",
            generationRequestId: Guid.NewGuid(),
            generationFingerprint: fp
        );
        await db.SceneImages.AddAsync(img1);
        await db.SaveChangesAsync();

        var img2 = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: Guid.NewGuid(),
            sceneRevision: 2,
            imageUrl: "https://cdn.project00.ai/images/2.png",
            prompt: "1man knight in room",
            generationRequestId: Guid.NewGuid(),
            generationFingerprint: fp
        );
        await db.SceneImages.AddAsync(img2);

        // Strictly asserts database rejects duplicate fingerprint with DbUpdateException
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
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

        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, requestId);
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

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

        // Compute expected attempt 1 fingerprint using the job's actual Id
        var fp = DeterministicSeedDerivation.ComputeFingerprint(
            job.Id, turnId, 1, 1, 100000L, snapshot.GenerationProfile.ParametersJson ?? string.Empty,
            "VisualIdentity", 1,
            compiler.CompileScenePrompt(snapshot), compiler.CompileNegativePrompt(snapshot), null);

        // Seed an active running attempt owned by worker-A with a valid lease
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
            leaseUntil: DateTime.UtcNow.AddMinutes(5)
        );
        await db.ImageGenerationAttempts.AddAsync(activeAttempt);
        await db.SaveChangesAsync();

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: characterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        // Worker-B attempts to handle the job
        var result = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-B", DateTime.UtcNow);

        // Must be deferred without invoking GPU!
        Assert.Equal(JobExecutionStatus.Deferred, result.Status);
        Assert.Equal(0, imageService.CallCount);
    }

    [Fact]
    public void DeterministicSeedDerivation_Fingerprint_SensitiveToPrompt_Negative_Workflow_And_Reference()
    {
        var jobId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();

        var fpBase = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, snapshotId, 1, 1, 12345L, "{\"weight\":0.12}",
            "VisualIdentity", 1, "prompt A", "neg A", "https://cdn/ref.png");

        var fpSame = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, snapshotId, 1, 1, 12345L, "{\"weight\":0.12}",
            "VisualIdentity", 1, "prompt A", "neg A", "https://cdn/ref.png");

        var fpDiffPrompt = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, snapshotId, 1, 1, 12345L, "{\"weight\":0.12}",
            "VisualIdentity", 1, "prompt B", "neg A", "https://cdn/ref.png");

        var fpDiffNegative = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, snapshotId, 1, 1, 12345L, "{\"weight\":0.12}",
            "VisualIdentity", 1, "prompt A", "neg B", "https://cdn/ref.png");

        var fpDiffRef = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, snapshotId, 1, 1, 12345L, "{\"weight\":0.12}",
            "VisualIdentity", 1, "prompt A", "neg A", "https://cdn/ref_diff.png");

        var fpDiffWorkflow = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, snapshotId, 1, 1, 12345L, "{\"weight\":0.12}",
            "VisualContinuity", 2, "prompt A", "neg A", "https://cdn/ref.png");

        Assert.Equal(fpBase, fpSame);
        Assert.NotEqual(fpBase, fpDiffPrompt);
        Assert.NotEqual(fpBase, fpDiffNegative);
        Assert.NotEqual(fpBase, fpDiffRef);
        Assert.NotEqual(fpBase, fpDiffWorkflow);
    }

    [Fact]
    public void DependencyInjection_WhenQualityGuardEnabledInProduction_WithStubEvaluator_ThrowsInvalidOperationException()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["AiProviders:ImageGeneration:QualityGuard:Enabled"] = "true",
            ["AiProviders:ImageGeneration:QualityGuard:AllowStubEvaluatorInProduction"] = "false",
            ["AiProviders:ImageProvider"] = "ComfyUI"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Infrastructure.DependencyInjection.AddInfrastructure(services, configuration));

        Assert.Contains("CRITICAL STARTUP CONFIGURATION ERROR", ex.Message);
        Assert.Contains("Production requires a genuine evaluator implementation", ex.Message);
    }

    [Fact]
    public void DependencyInjection_WhenQualityGuardEnabledInProduction_WithExplicitAllowStubOptIn_Succeeds()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["AiProviders:ImageGeneration:QualityGuard:Enabled"] = "true",
            ["AiProviders:ImageGeneration:QualityGuard:AllowStubEvaluatorInProduction"] = "true",
            ["AiProviders:ImageProvider"] = "ComfyUI"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var result = Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ImageGenerationJobHandler_WhenAttempt1DegradedAndAttempt2Passes_RecordsDegradedForAttempt1AndSucceededForAttempt2()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var imageService = new FakeImageService();
        var evaluator = new SequenceEvaluator(new[]
        {
            IdentityEvaluationResult.Degrade(0.65f, 0.80f, 0.72f, new[] {
                new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "IDENTITY_SIMILARITY_DEGRADED", "Degraded face", false)
            }),
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

        var result = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow);
        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.Equal(2, imageService.CallCount);

        var attempts = await db.ImageGenerationAttempts.OrderBy(a => a.AttemptNumber).ToListAsync();
        Assert.Equal(2, attempts.Count);

        // Attempt 1 must be Degraded!
        Assert.Equal(1, attempts[0].AttemptNumber);
        Assert.Equal(GenerationAttemptStatus.Degraded, attempts[0].Status);
        Assert.Equal(0.65f, attempts[0].IdentitySimilarity);

        // Attempt 2 must be Succeeded!
        Assert.Equal(2, attempts[1].AttemptNumber);
        Assert.Equal(GenerationAttemptStatus.Succeeded, attempts[1].Status);
        Assert.Equal(0.85f, attempts[1].IdentitySimilarity);

        var sceneImage = await db.SceneImages.FirstOrDefaultAsync();
        Assert.NotNull(sceneImage);
        Assert.True(sceneImage.IsCurrent);
    }

    [Fact]
    public async Task ImageGenerationJobHandler_WhenAllAttemptsExhausted_RecordsDegradedForEachAttemptAndQuarantinesSceneImage()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);

        var compiler = new FakePromptCompiler("1man knight", "1girl");
        var imageService = new FakeImageService();
        var evaluator = new SequenceEvaluator(new[]
        {
            IdentityEvaluationResult.Degrade(0.60f, 0.40f, 0.50f, new[] {
                new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "IDENTITY_SIMILARITY_DEGRADED", "Bad sim", false)
            })
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
        Assert.Equal(3, imageService.CallCount);

        var attempts = await db.ImageGenerationAttempts.OrderBy(a => a.AttemptNumber).ToListAsync();
        Assert.Equal(3, attempts.Count);

        foreach (var att in attempts)
        {
            Assert.Equal(GenerationAttemptStatus.Degraded, att.Status);
        }

        var sceneImage = await db.SceneImages.FirstOrDefaultAsync();
        Assert.NotNull(sceneImage);
        Assert.False(sceneImage.IsCurrent); // Quarantined from continuity lineage!
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

        // First run completes and logs attempt in ImageGenerationAttempts ledger
        var result1 = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow);
        Assert.Equal(JobExecutionStatus.Completed, result1.Status);
        Assert.Equal(1, imageService.CallCount);

        var recordedAttempt = await db.ImageGenerationAttempts.FirstOrDefaultAsync();
        Assert.NotNull(recordedAttempt);
        Assert.Equal(GenerationAttemptStatus.Succeeded, recordedAttempt.Status);
        Assert.NotEmpty(recordedAttempt.GenerationFingerprint);

        // Simulated Worker crash & restart: second worker runs same generation job
        var result2 = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-2", DateTime.UtcNow);
        
        // Reuses existing succeeded attempt or marks skipped: Zero additional GPU calls!
        Assert.Equal(1, imageService.CallCount);
    }

    [Fact]
    public void ImageGenerationJobHandler_Constructor_WhenEvaluatorNull_ThrowsArgumentNullException()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ProjectDbContext(options);

        var compiler = new FakePromptCompiler("pos", "neg");
        var imageService = new FakeImageService();

        Assert.Throws<ArgumentNullException>(() => new ImageGenerationJobHandler(
            dbContext: db,
            visualCompiler: compiler,
            imageService: imageService,
            logger: NullLogger<ImageGenerationJobHandler>.Instance,
            dateTimeProvider: new SystemDateTimeProvider(),
            qualityEvaluator: null!
        ));
    }

    [Fact]
    public void IdentityMitigationProfileResolver_Attempt1_Pass_KeepsOriginalProfileAndBaseSeed()
    {
        long baseSeed = 424242L;
        var initialParametersJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            ipAdapter = new { weight = 0.60, endAt = 0.85 },
            sceneContinuity = new { weight = 0.12, endAt = 0.25, weightType = "style transfer" }
        });
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: baseSeed, parametersJson: initialParametersJson)
        );

        var (profile1, seed1) = IdentityMitigationProfileResolver.ResolveMitigation(
            snapshot, QualityMitigationAction.Pass, 1, baseSeed);

        Assert.Equal(baseSeed, seed1);
        Assert.Equal(initialParametersJson, profile1.ParametersJson);
    }

    [Fact]
    public void IdentityMitigationProfileResolver_Attempt2_RetryAttenuated_AppliesAttenuationAndDerivedSeed()
    {
        long baseSeed = 424242L;
        var initialParametersJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            ipAdapter = new { weight = 0.60, endAt = 0.85 },
            sceneContinuity = new { weight = 0.12, endAt = 0.25, weightType = "style transfer" }
        });
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: baseSeed, parametersJson: initialParametersJson)
        );

        var (profile2, seed2) = IdentityMitigationProfileResolver.ResolveMitigation(
            snapshot, QualityMitigationAction.RetryAttenuated, 2, baseSeed);

        long expectedSeed2 = DeterministicSeedDerivation.Derive(baseSeed, 2);
        Assert.Equal(expectedSeed2, seed2);
        Assert.NotEqual(baseSeed, seed2);

        using var doc = System.Text.Json.JsonDocument.Parse(profile2.ParametersJson!);
        var ip = doc.RootElement.GetProperty("ipAdapter");
        var cont = doc.RootElement.GetProperty("sceneContinuity");

        Assert.Equal(0.65f, (float)ip.GetProperty("weight").GetDouble(), 2);
        Assert.Equal(0.85f, (float)ip.GetProperty("endAt").GetDouble(), 2);
        Assert.Equal(0.06f, (float)cont.GetProperty("weight").GetDouble(), 2);
        Assert.Equal(0.15f, (float)cont.GetProperty("endAt").GetDouble(), 2);
        Assert.Equal("style transfer", cont.GetProperty("weightType").GetString());
    }

    [Fact]
    public void IdentityMitigationProfileResolver_Attempt3_RetryIsolated_AppliesIsolationAndDerivedSeed()
    {
        long baseSeed = 424242L;
        var initialParametersJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            ipAdapter = new { weight = 0.60, endAt = 0.85 },
            sceneContinuity = new { weight = 0.12, endAt = 0.25, weightType = "style transfer" }
        });
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: baseSeed, parametersJson: initialParametersJson)
        );

        var (profile3, seed3) = IdentityMitigationProfileResolver.ResolveMitigation(
            snapshot, QualityMitigationAction.RetryIsolated, 3, baseSeed);

        long expectedSeed3 = DeterministicSeedDerivation.Derive(baseSeed, 3);
        Assert.Equal(expectedSeed3, seed3);
        Assert.NotEqual(baseSeed, seed3);

        using var doc = System.Text.Json.JsonDocument.Parse(profile3.ParametersJson!);
        var ip = doc.RootElement.GetProperty("ipAdapter");
        var cont = doc.RootElement.GetProperty("sceneContinuity");

        Assert.Equal(0.70f, (float)ip.GetProperty("weight").GetDouble(), 2);
        Assert.Equal(0.85f, (float)ip.GetProperty("endAt").GetDouble(), 2);
        Assert.Equal(0.0f, (float)cont.GetProperty("weight").GetDouble(), 2);
        Assert.Equal(0.0f, (float)cont.GetProperty("endAt").GetDouble(), 2);
        Assert.Equal("style transfer", cont.GetProperty("weightType").GetString());
    }

    [Fact]
    public void IdentityMitigationProfileResolver_AttemptsAreDeterministicAndDistinct()
    {
        long baseSeed = 999999L;
        var initialParametersJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            ipAdapter = new { weight = 0.60, endAt = 0.85 },
            sceneContinuity = new { weight = 0.12, endAt = 0.25, weightType = "style transfer" }
        });
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: baseSeed, parametersJson: initialParametersJson)
        );

        var (p1, s1) = IdentityMitigationProfileResolver.ResolveMitigation(snapshot, QualityMitigationAction.Pass, 1, baseSeed);
        var (p2, s2) = IdentityMitigationProfileResolver.ResolveMitigation(snapshot, QualityMitigationAction.RetryAttenuated, 2, baseSeed);
        var (p3, s3) = IdentityMitigationProfileResolver.ResolveMitigation(snapshot, QualityMitigationAction.RetryIsolated, 3, baseSeed);

        // Attempt 2 and Attempt 3 are distinct in seeds and parameters
        Assert.NotEqual(s2, s3);
        Assert.NotEqual(p2.ParametersJson, p3.ParametersJson);

        // Determinism: calling again produces identical results
        var (p2b, s2b) = IdentityMitigationProfileResolver.ResolveMitigation(snapshot, QualityMitigationAction.RetryAttenuated, 2, baseSeed);
        var (p3b, s3b) = IdentityMitigationProfileResolver.ResolveMitigation(snapshot, QualityMitigationAction.RetryIsolated, 3, baseSeed);

        Assert.Equal(s2, s2b);
        Assert.Equal(p2.ParametersJson, p2b.ParametersJson);
        Assert.Equal(s3, s3b);
        Assert.Equal(p3.ParametersJson, p3b.ParametersJson);

        // Fingerprints for attempts are distinct
        var fp1 = DeterministicSeedDerivation.ComputeFingerprint(Guid.Empty, snapshot.TurnId, 1, 1, s1, p1.ParametersJson!, "VisualIdentity", 1, "prompt", "neg", null);
        var fp2 = DeterministicSeedDerivation.ComputeFingerprint(Guid.Empty, snapshot.TurnId, 1, 2, s2, p2.ParametersJson!, "VisualIdentity", 1, "prompt", "neg", null);
        var fp3 = DeterministicSeedDerivation.ComputeFingerprint(Guid.Empty, snapshot.TurnId, 1, 3, s3, p3.ParametersJson!, "VisualIdentity", 1, "prompt", "neg", null);

        Assert.NotEqual(fp1, fp2);
        Assert.NotEqual(fp2, fp3);
        Assert.NotEqual(fp1, fp3);

        // Fingerprint determinism
        var fp2_repeat = DeterministicSeedDerivation.ComputeFingerprint(Guid.Empty, snapshot.TurnId, 1, 2, s2b, p2b.ParametersJson!, "VisualIdentity", 1, "prompt", "neg", null);
        Assert.Equal(fp2, fp2_repeat);
    }

    private sealed class FakePromptCompiler : IVisualPromptCompiler
    {
        private readonly string _pos;
        private readonly string _neg;
        public FakePromptCompiler(string pos, string neg) { _pos = pos; _neg = neg; }
        public string CompileAvatarPrompt(Character character) => _pos;
        public string CompileScenePrompt(Character character, SceneContext scene, CharacterRelationship? relationship = null, Slot2Context context = Slot2Context.SameScene) => _pos;
        public string CompileScenePrompt(VisualSnapshot snapshot) => _pos;
        public string CompileNegativePrompt(VisualSnapshot snapshot, string? customNegative = null) => _neg;
        public string CompileNegativePrompt(CharacterVisualIdentity? identity, string? customNegative = null) => _neg;
    }

    private sealed class FakeImageService : IImageGenerationService
    {
        public int CallCount { get; private set; } = 0;
        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default) =>
            Task.FromResult($"https://cdn.project00.ai/images/gen_attempt_{++CallCount}.png");
        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default) =>
            Task.FromResult($"https://cdn.project00.ai/images/gen_attempt_{++CallCount}.png");
        public Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new ImageGenerationResult(
                ImageUrl: $"https://cdn.project00.ai/images/gen_attempt_{CallCount}.png",
                Provider: "ComfyUI",
                ProviderJobId: $"job_{CallCount}",
                DurationMs: 1000,
                Seed: request.Seed ?? 100000L
            ));
        }
    }

    private sealed class SequenceEvaluator : IIdentityQualityEvaluator
    {
        private readonly IReadOnlyList<IdentityEvaluationResult> _results;
        public int CallCount { get; private set; } = 0;
        public SequenceEvaluator(IReadOnlyList<IdentityEvaluationResult> results) { _results = results; }
        public Task<IdentityEvaluationResult> EvaluateAsync(string imageLocation, VisualSnapshot snapshot, CancellationToken ct = default)
        {
            int idx = Math.Min(CallCount, _results.Count - 1);
            CallCount++;
            return Task.FromResult(_results[idx]);
        }
    }
}
