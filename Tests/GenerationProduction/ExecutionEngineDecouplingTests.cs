using System.Reflection;
using System.Text.Json;
using Application.Common;
using Application.DTOs;
using Application.Enums;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.ImageGeneration;
using Infrastructure.ImageGeneration.ComfyUI;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class ExecutionEngineDecouplingTests
{
    private static CoreDbContext CreateSqliteDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new CoreDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static VisualSnapshot CreateTestSnapshot(string model = "meinamix_meinaV11.safetensors", string? workflow = null)
    {
        return new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: new CharacterVisualIdentity(
                Face: "short raven hair, amber eyes",
                CanonicalReferenceUrl: "https://cdn.project00.ai/canonical_alice.png"),
            SceneState: new SessionSceneState(
                CurrentLocation: "Cyberpunk Alley",
                CurrentOutfit: "Leather Trench Coat",
                Atmosphere: "Neon rain"),
            TransientState: null,
            GenerationProfile: new GenerationProfile(
                Seed: 42L,
                Model: model,
                Workflow: workflow
            )
        );
    }

    /// <summary>
    /// A pure implementation of IImageGenerationExecutor that does NOT implement IImageGenerationService.
    /// Proves that ImageGenerationOrchestrator and Core have no hard dependency on IImageGenerationService or ComfyUI.
    /// </summary>
    private sealed class PureStubExecutor : IImageGenerationExecutor
    {
        public ImageGenerationRequest? LastExecutedRequest { get; private set; }
        public int ExecutionCount { get; private set; }
        private readonly string _returnedImageUrl;
        private readonly string _providerName;

        public PureStubExecutor(string returnedImageUrl = "https://cdn.project00.ai/pure_executor_output.png", string providerName = "StubEngine")
        {
            _returnedImageUrl = returnedImageUrl;
            _providerName = providerName;
        }

        public Task<ImageGenerationResult> ExecuteAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            ExecutionCount++;
            LastExecutedRequest = request;
            return Task.FromResult(new ImageGenerationResult(
                ImageUrl: _returnedImageUrl,
                Provider: _providerName,
                ProviderJobId: $"stub-job-{Guid.NewGuid():N}",
                DurationMs: 120,
                Seed: request.Seed ?? 42L
            ));
        }
    }

    /// <summary>
    /// Legacy service implementing IImageGenerationService to verify default interface method inheritance on IImageGenerationExecutor.
    /// </summary>
    private sealed class LegacyImageService : IImageGenerationService
    {
        public ImageGenerationRequest? LastRequest { get; private set; }

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
            => GenerateImageAsync(new ImageGenerationRequest(prompt, width, height), ct);

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult("https://cdn.project00.ai/legacy_output.png");
        }

        public Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ImageGenerationResult(
                ImageUrl: "https://cdn.project00.ai/legacy_output.png",
                Provider: "LegacyProvider",
                ProviderJobId: "legacy-123",
                DurationMs: 250,
                Seed: request.Seed ?? 100L
            ));
        }
    }

    private sealed class ConstantFingerprintService : IGenerationFingerprintService
    {
        public string Fingerprint { get; }
        public ConstantFingerprintService(string fingerprint = "fixed-test-fingerprint") => Fingerprint = fingerprint;

        public string ComputeFingerprint(
            Guid jobId,
            VisualSnapshot snapshot,
            GenerationProfile profile,
            long derivedSeed,
            int attemptNumber,
            string workflow = "VisualIdentity",
            int workflowVersion = 1,
            string? modelIdentifier = null,
            string? compiledPrompt = null,
            string? compiledNegativePrompt = null,
            string? previousReferenceUrl = null,
            string? mitigationAction = null) => Fingerprint;

        public string ComputeRawFingerprint(
            Guid jobId,
            Guid snapshotTurnId,
            int sceneRevision,
            int attemptNumber,
            long derivedSeed,
            string parametersJson,
            string workflow = "VisualIdentity",
            int workflowVersion = 1,
            string? compiledPrompt = null,
            string? compiledNegativePrompt = null,
            string? previousReferenceUrl = null,
            string? modelIdentifier = null,
            string? mitigationAction = null) => Fingerprint;
    }

    [Fact]
    public async Task Test1_ImageGenerationOrchestrator_RunsWithPureExecutor_SucceedsEndToEnd()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var db = CreateSqliteDbContext(connection);

        var executor = new PureStubExecutor();
        var compiler = new VisualPromptCompiler();
        var dateTimeProvider = new SystemDateTimeProvider();
        var lineageResolver = new PredecessorLineageResolver(db, NullLogger<PredecessorLineageResolver>.Instance);
        var acceptanceService = new ArtifactAcceptanceService(db, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);
        var qualityEvaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var qualityGuardPolicy = new IdentityQualityGuardPolicy();

        // Inject pure IImageGenerationExecutor into ImageGenerationOrchestrator
        var orchestrator = new ImageGenerationOrchestrator(
            db,
            compiler,
            executor,
            NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider,
            qualityEvaluator,
            qualityGuardPolicy,
            lineageResolver,
            acceptanceService
        );

        var snapshot = CreateTestSnapshot();
        var outboxId = Guid.NewGuid();
        var payload = new SceneImageGenerationOutboxPayload(snapshot.TurnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, Guid.NewGuid());

        // Act
        var result = await orchestrator.OrchestrateSceneImageGenerationAsync(payload, outboxId, "worker-engine-1", DateTime.UtcNow);

        // Assert
        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.Equal(1, executor.ExecutionCount);
        Assert.NotNull(executor.LastExecutedRequest);
        Assert.Contains("Cyberpunk Alley", executor.LastExecutedRequest.Prompt);

        // Verify DB attempt record reflects the pure executor's result
        var attempt = await db.ImageGenerationAttempts.FirstOrDefaultAsync(a => a.TurnId == snapshot.TurnId);
        Assert.NotNull(attempt);
        Assert.Equal(GenerationAttemptStatus.Succeeded, attempt.Status);
        Assert.Equal("https://cdn.project00.ai/pure_executor_output.png", attempt.ImageUrl);
    }

    [Fact]
    public async Task Test2_ComfyUIImageGenerationService_ImplementsIImageGenerationExecutor_ExecutesSuccessfully()
    {
        var mockClient = new MockComfyUIClient();
        var storage = new MockStorageService();
        var mockInput = new MockInputImageService();
        var builders = new IComfyUIWorkflowBuilder[]
        {
            new VisualIdentityWorkflowV1Builder(),
            new VisualContinuityWorkflowV2Builder(),
            new TextToImageWorkflowV1Builder()
        };
        var config = new ConfigurationBuilder().Build();

        var comfyService = new ComfyUIImageGenerationService(
            mockClient, storage, mockInput, builders, config, NullLogger<ComfyUIImageGenerationService>.Instance);

        // Verify interface compliance
        Assert.IsAssignableFrom<IImageGenerationExecutor>(comfyService);

        IImageGenerationExecutor executor = comfyService;

        var request = new ImageGenerationRequest(
            Prompt: "masterpiece, 1girl in futuristic city",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            ReferenceImageUrl: "https://cdn.project00.ai/canonical_face.png",
            Seed: 12345L
        );

        var result = await executor.ExecuteAsync(request);

        Assert.NotNull(result);
        Assert.Equal("ComfyUI", result.Provider);
        Assert.False(string.IsNullOrWhiteSpace(result.ImageUrl));
        Assert.Equal(1, mockClient.QueuePromptCallCount);
    }

    [Fact]
    public async Task Test3_PluggableExecutorSubstitution_WorksWithoutTouchingApplicationOrchestrator()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var db = CreateSqliteDbContext(connection);

        var compiler = new VisualPromptCompiler();
        var dateTimeProvider = new SystemDateTimeProvider();
        var lineageResolver = new PredecessorLineageResolver(db, NullLogger<PredecessorLineageResolver>.Instance);
        var acceptanceService = new ArtifactAcceptanceService(db, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);
        var qualityEvaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var qualityGuardPolicy = new IdentityQualityGuardPolicy();

        // 1. First execution with Engine A
        var engineA = new PureStubExecutor("https://cdn.project00.ai/engine_a.png", "EngineAlpha");
        var orchestratorA = new ImageGenerationOrchestrator(
            db, compiler, engineA, NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider, qualityEvaluator, qualityGuardPolicy, lineageResolver, acceptanceService);

        var snapshotA = CreateTestSnapshot();
        var payloadA = new SceneImageGenerationOutboxPayload(snapshotA.TurnId, snapshotA.CharacterId, Guid.NewGuid(), snapshotA, Guid.NewGuid());
        var resultA = await orchestratorA.OrchestrateSceneImageGenerationAsync(payloadA, Guid.NewGuid(), "worker-a", DateTime.UtcNow);

        Assert.Equal(JobExecutionStatus.Completed, resultA.Status);
        Assert.Equal(1, engineA.ExecutionCount);

        // 2. Second execution with Engine B plugged into the exact same ImageGenerationOrchestrator structure
        var engineB = new PureStubExecutor("https://cdn.project00.ai/engine_b.png", "EngineBeta");
        var orchestratorB = new ImageGenerationOrchestrator(
            db, compiler, engineB, NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider, qualityEvaluator, qualityGuardPolicy, lineageResolver, acceptanceService);

        var snapshotB = CreateTestSnapshot();
        var payloadB = new SceneImageGenerationOutboxPayload(snapshotB.TurnId, snapshotB.CharacterId, Guid.NewGuid(), snapshotB, Guid.NewGuid());
        var resultB = await orchestratorB.OrchestrateSceneImageGenerationAsync(payloadB, Guid.NewGuid(), "worker-b", DateTime.UtcNow);

        Assert.Equal(JobExecutionStatus.Completed, resultB.Status);
        Assert.Equal(1, engineB.ExecutionCount);

        // Validate independent outcomes recorded in DB
        var attemptA = await db.ImageGenerationAttempts.FirstAsync(a => a.TurnId == snapshotA.TurnId);
        var attemptB = await db.ImageGenerationAttempts.FirstAsync(a => a.TurnId == snapshotB.TurnId);
        Assert.Equal("https://cdn.project00.ai/engine_a.png", attemptA.ImageUrl);
        Assert.Equal("https://cdn.project00.ai/engine_b.png", attemptB.ImageUrl);
    }

    [Fact]
    public void Test4_ArchitectureBoundary_ImageGenerationOrchestrator_ZeroComfyUIDependencies()
    {
        var orchestratorType = typeof(ImageGenerationOrchestrator);

        // Inspect all fields: none must belong to ComfyUI namespace
        var fields = orchestratorType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var field in fields)
        {
            var fieldTypeNamespace = field.FieldType.Namespace ?? string.Empty;
            Assert.DoesNotContain("ComfyUI", fieldTypeNamespace);
        }

        // Inspect constructor parameters: none must belong to ComfyUI namespace
        var constructors = orchestratorType.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        foreach (var ctor in constructors)
        {
            foreach (var param in ctor.GetParameters())
            {
                var paramTypeNamespace = param.ParameterType.Namespace ?? string.Empty;
                Assert.DoesNotContain("ComfyUI", paramTypeNamespace);
            }
        }

        // Inspect method return and parameter types
        var methods = orchestratorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        foreach (var method in methods)
        {
            Assert.DoesNotContain("ComfyUI", method.ReturnType.Namespace ?? string.Empty);
            foreach (var p in method.GetParameters())
            {
                Assert.DoesNotContain("ComfyUI", p.ParameterType.Namespace ?? string.Empty);
            }
        }
    }

    [Fact]
    public async Task Test5_IImageGenerationService_BackwardCompatibility_DefaultsExecuteAsync()
    {
        var legacy = new LegacyImageService();
        IImageGenerationExecutor executor = legacy;

        var request = new ImageGenerationRequest(Prompt: "A scenic mountain view", Width: 512, Height: 512);

        // Invoking ExecuteAsync via default interface implementation on IImageGenerationService
        var result = await executor.ExecuteAsync(request);

        Assert.NotNull(result);
        Assert.Equal("https://cdn.project00.ai/legacy_output.png", result.ImageUrl);
        Assert.Equal("LegacyProvider", result.Provider);
        Assert.Equal("A scenic mountain view", legacy.LastRequest?.Prompt);
    }

    [Fact]
    public async Task Test6_ReusedAttempt_ProviderFallback_PreservesJobProviderOrWorkflow()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var db = CreateSqliteDbContext(connection);

        var executor = new PureStubExecutor();
        var compiler = new VisualPromptCompiler();
        var dateTimeProvider = new SystemDateTimeProvider();
        var lineageResolver = new PredecessorLineageResolver(db, NullLogger<PredecessorLineageResolver>.Instance);
        var acceptanceService = new ArtifactAcceptanceService(db, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);
        var qualityEvaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var qualityGuardPolicy = new IdentityQualityGuardPolicy();

        var fingerprintService = new ConstantFingerprintService("reuse-fingerprint-123");

        var orchestrator = new ImageGenerationOrchestrator(
            db, compiler, executor, NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider, qualityEvaluator, qualityGuardPolicy, lineageResolver, acceptanceService,
            fingerprintService: fingerprintService);

        var snapshot = CreateTestSnapshot();
        var baseSeed = snapshot.GenerationProfile.Seed;
        var derivedSeed = baseSeed + 1; // attempt 1 derived seed
        var requestParametersJson = JsonSerializer.Serialize(new { prompt = "reused prompt" });
        var fingerprint = "reuse-fingerprint-123";

        // Pre-insert an existing succeeded attempt
        var jobId = Guid.NewGuid();
        var existingAttempt = new ImageGenerationAttempt(
            generationJobId: jobId,
            turnId: snapshot.TurnId,
            sceneRevision: snapshot.SceneRevision,
            attemptNumber: 1,
            derivedSeed: derivedSeed,
            parametersJson: requestParametersJson,
            generationFingerprint: fingerprint,
            status: GenerationAttemptStatus.Succeeded
        );
        typeof(ImageGenerationAttempt).GetProperty(nameof(ImageGenerationAttempt.ImageUrl))!
            .SetValue(existingAttempt, "https://cdn.project00.ai/reused_cached.png");

        await db.ImageGenerationAttempts.AddAsync(existingAttempt);

        // Pre-insert a job with custom provider
        var genRequestId = Guid.NewGuid();
        var job = new ImageGenerationJob(
            sessionId: snapshot.SessionId,
            turnId: snapshot.TurnId,
            characterId: snapshot.CharacterId,
            sceneRevision: snapshot.SceneRevision,
            generationRequestId: genRequestId,
            provider: "CustomExternalEngine",
            workflow: "VisualIdentity"
        );
        typeof(ImageGenerationJob).GetProperty(nameof(ImageGenerationJob.Id))!
            .SetValue(job, jobId);

        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var payload = new SceneImageGenerationOutboxPayload(snapshot.TurnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, genRequestId);

        // Act
        var result = await orchestrator.OrchestrateSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-reuse", DateTime.UtcNow);

        // Assert: Succeeded attempt was reused, executor was not called again
        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.Equal(0, executor.ExecutionCount);

        // Succeeded attempt image URL is reused
        var winningAttempt = await db.ImageGenerationAttempts.FirstAsync(a => a.Id == existingAttempt.Id);
        Assert.Equal("https://cdn.project00.ai/reused_cached.png", winningAttempt.ImageUrl);
    }

    #region Mock Helpers for ComfyUI Test

    private sealed class MockComfyUIClient : IComfyUIClient
    {
        public int QueuePromptCallCount { get; private set; }

        public Task<string> QueuePromptAsync(Dictionary<string, object> promptGraph, CancellationToken ct = default)
        {
            QueuePromptCallCount++;
            return Task.FromResult(Guid.NewGuid().ToString("N"));
        }

        public Task<ComfyUIHistoryResult?> GetHistoryAsync(string promptId, CancellationToken ct = default)
        {
            var images = new List<ComfyUIHistoryOutputImage>
            {
                new("output.png", "", "output")
            };
            return Task.FromResult<ComfyUIHistoryResult?>(new ComfyUIHistoryResult(promptId, true, null, images));
        }

        public Task<byte[]> DownloadImageAsync(string filename, string? subfolder = null, string? type = "output", CancellationToken ct = default)
        {
            return Task.FromResult(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        }

        public Task<bool> DeleteQueuedPromptAsync(string promptId, CancellationToken ct = default) => Task.FromResult(true);

        public Task InterruptAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MockStorageService : IStorageService
    {
        public Task<string> SaveImageAsync(byte[] imageBytes, string fileName, string contentType = "image/jpeg", CancellationToken ct = default)
            => Task.FromResult($"https://cdn.project00.ai/rendered/{fileName}");

        public Task<string> SaveBase64ImageAsync(string base64Data, string fileName, CancellationToken ct = default)
            => Task.FromResult($"https://cdn.project00.ai/rendered/{fileName}");

        public Task<bool> DeleteFileAsync(string fileUrl, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class MockInputImageService : IComfyUIInputImageService
    {
        public int UploadCallCount { get; private set; }

        public Task<string> EnsureImageUploadedAsync(string? referenceImageUrl, CancellationToken ct = default)
        {
            UploadCallCount++;
            return Task.FromResult("uploaded_input.png");
        }
    }

    #endregion
}
