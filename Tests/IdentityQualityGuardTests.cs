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
    [InlineData(0.65f, 0.85f, false, IdentityStatus.Degraded)] // Face below 0.75
    [InlineData(0.80f, 0.40f, false, IdentityStatus.Degraded)] // Feature below 0.50
    [InlineData(0.80f, 0.85f, true, IdentityStatus.Failed)]    // Hard Invariant violation
    public void IdentityQualityGuardPolicy_EvaluateStatus_CategorizesStatusAccurately(
        float faceSim, float featScore, bool invariantViolated, IdentityStatus expectedStatus)
    {
        var policy = new IdentityQualityGuardPolicy(MinAcceptableFaceSimilarity: 0.75f, MinAcceptableFeatureScore: 0.50f);
        var status = policy.EvaluateStatus(faceSim, featScore, invariantViolated, out var violations);

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

        var policy = new IdentityQualityGuardPolicy(MinAcceptableFaceSimilarity: 0.75f, MaxAttempts: 3);
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

        var policy = new IdentityQualityGuardPolicy(MinAcceptableFaceSimilarity: 0.75f, MaxAttempts: 3);
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
        var policy = new IdentityQualityGuardPolicy(MinAcceptableFaceSimilarity: 0.75f, MaxAttempts: 3);

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
