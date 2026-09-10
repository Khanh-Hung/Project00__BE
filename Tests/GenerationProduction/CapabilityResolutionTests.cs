using Application.Common;
using Application.DTOs;
using Application.Enums;
using Application.Exceptions;
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

public sealed class CapabilityResolutionTests
{
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

    private static CoreDbContext CreateSqliteDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new CoreDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static VisualSnapshot CreateTestSnapshot(string model, CharacterVisualIdentity? identity = null)
    {
        return VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            sceneRevision: 1,
            visualIdentity: identity ?? new CharacterVisualIdentity(
                Presentation: GenderPresentation.Female,
                Hair: "silver hair",
                Eyes: "crimson eyes",
                Style: "Anime",
                VisualStyle: VisualStyle.Anime
            ),
            sceneState: new SessionSceneState(
                CurrentLocation: "Garden",
                CurrentPosition: "Center",
                CurrentOutfit: "Dress",
                CurrentTimeOfDay: "Day",
                HeldItems: "Rose",
                Atmosphere: "Peaceful",
                SceneRevision: 1,
                LastUpdatedAt: DateTime.UtcNow
            ),
            transientState: new TransientVisualState(
                Expression: "smiling",
                Gaze: "looking at viewer",
                Pose: "standing"
            ),
            generationProfile: GenerationProfile.CreateDefault(model: model, workflow: "VisualIdentity", workflowVersion: 1)
        );
    }

    // =========================================================================
    // Test 1: Supported Model -> Accepted
    // =========================================================================
    [Fact]
    public void Test1_SupportedModel_VisualIdentityV1_IsAcceptedByBuilder_AndExposesCapability()
    {
        var builder = new VisualIdentityWorkflowV1Builder();
        const string supportedModel = "meinamix_meinaV11.safetensors";

        var capability = new ImageGenerationCapability(supportedModel, "VisualIdentity", 1);

        // Assert builder accepts capability
        Assert.True(builder.CanHandle(capability));
        Assert.True(builder.CanHandle("VisualIdentity", 1, supportedModel));

        // Case insensitivity check
        var upperCapability = new ImageGenerationCapability(supportedModel.ToUpperInvariant(), "VisualIdentity", 1);
        Assert.True(builder.CanHandle(upperCapability));

        // Profile mapping check
        var profile = GenerationProfile.CreateDefault(model: supportedModel, workflow: "VisualIdentity", workflowVersion: 1);
        Assert.Equal(capability, profile.Capability);
        Assert.True(builder.CanHandle(profile.Capability));

        // Request mapping check
        var request = new ImageGenerationRequest(
            Prompt: "masterpiece",
            Model: supportedModel,
            Workflow: "VisualIdentity",
            WorkflowVersion: 1
        );
        Assert.Equal(capability, request.Capability);
        Assert.True(builder.CanHandle(request.Capability));
    }

    // =========================================================================
    // Test 2: Unsupported Model -> Rejected
    // =========================================================================
    [Fact]
    public void Test2_UnsupportedModel_IsRejectedByBuilder()
    {
        var builder = new VisualIdentityWorkflowV1Builder();

        // Unknown model
        var unsupportedCap = new ImageGenerationCapability("sdxl_base_1.0.safetensors", "VisualIdentity", 1);
        Assert.False(builder.CanHandle(unsupportedCap));

        // Another unsupported architecture
        var fluxCap = new ImageGenerationCapability("flux1-dev.safetensors", "VisualIdentity", 1);
        Assert.False(builder.CanHandle(fluxCap));

        // Null / whitespace model
        var emptyCap = new ImageGenerationCapability("", "VisualIdentity", 1);
        Assert.False(builder.CanHandle(emptyCap));

        var whitespaceCap = new ImageGenerationCapability("   ", "VisualIdentity", 1);
        Assert.False(builder.CanHandle(whitespaceCap));
    }

    // =========================================================================
    // Test 3: Unsupported Workflow or Version -> Rejected
    // =========================================================================
    [Fact]
    public void Test3_UnsupportedWorkflowOrVersion_IsRejected()
    {
        var v1Builder = new VisualIdentityWorkflowV1Builder();
        var v2Builder = new VisualContinuityWorkflowV2Builder();
        var t2iBuilder = new TextToImageWorkflowV1Builder();
        const string model = "meinamix_meinaV11.safetensors";

        // 3a. Unknown workflow
        var unknownWfCap = new ImageGenerationCapability(model, "NonExistentWorkflow", 1);
        Assert.False(v1Builder.CanHandle(unknownWfCap));
        Assert.False(v2Builder.CanHandle(unknownWfCap));
        Assert.False(t2iBuilder.CanHandle(unknownWfCap));

        // 3b. Unsupported version: VisualIdentity with version 2 (VisualIdentity is v1)
        var viV2Cap = new ImageGenerationCapability(model, "VisualIdentity", 2);
        Assert.False(v1Builder.CanHandle(viV2Cap));

        // 3c. Unsupported version: VisualContinuity with version 1 (VisualContinuity is v2)
        var vcV1Cap = new ImageGenerationCapability(model, "VisualContinuity", 1);
        Assert.False(v2Builder.CanHandle(vcV1Cap));

        // 3d. Unsupported version: TextToImage with version 2 (TextToImage is v1)
        var t2iV2Cap = new ImageGenerationCapability(model, "TextToImage", 2);
        Assert.False(t2iBuilder.CanHandle(t2iV2Cap));
    }

    // =========================================================================
    // Test 4: Fail Before Provider -> Zero QueuePrompt Calls
    // =========================================================================
    [Fact]
    public async Task Test4a_UnsupportedModel_GuaranteesZeroQueuePromptAndZeroUploadCalls_InComfyUIService()
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

        var service = new ComfyUIImageGenerationService(
            mockClient, storage, mockInput, builders, config, NullLogger<ComfyUIImageGenerationService>.Instance);

        var request = new ImageGenerationRequest(
            Prompt: "test",
            Model: "unsupported_model_v9.safetensors",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            ReferenceImageUrl: "https://cdn.project00.ai/ref.png"
        );

        // Act & Assert
        var ex = await Assert.ThrowsAsync<GpuNonTransientException>(() =>
            service.GenerateImageWithResultAsync(request));

        Assert.Contains("is not compatible with model", ex.Message);
        Assert.Contains("unsupported_model_v9.safetensors", ex.Message);

        // Verify invariant: Zero network requests to ComfyUI QueuePrompt or Input Image Upload
        Assert.Equal(0, mockClient.QueuePromptCallCount);
        Assert.Equal(0, mockInput.UploadCallCount);
    }

    [Fact]
    public async Task Test4b_UnsupportedWorkflow_GuaranteesZeroQueuePromptCalls_InComfyUIService()
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

        var service = new ComfyUIImageGenerationService(
            mockClient, storage, mockInput, builders, config, NullLogger<ComfyUIImageGenerationService>.Instance);

        var request = new ImageGenerationRequest(
            Prompt: "test",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "FuturisticWorkflow3D",
            WorkflowVersion: 1,
            ReferenceImageUrl: "https://cdn.project00.ai/ref.png"
        );

        var ex = await Assert.ThrowsAsync<GpuNonTransientException>(() =>
            service.GenerateImageWithResultAsync(request));

        Assert.Contains("is not compatible with model", ex.Message);
        Assert.Equal(0, mockClient.QueuePromptCallCount);
        Assert.Equal(0, mockInput.UploadCallCount);
    }

    [Fact]
    public async Task Test4c_ImageGenerationOrchestrator_FailsTerminally_WithoutQueuePrompt_WhenModelUnsupported()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        using var db = CreateSqliteDbContext(connection);

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

        var compiler = new VisualPromptCompiler();
        var dateTimeProvider = new SystemDateTimeProvider();
        var lineageResolver = new PredecessorLineageResolver(db, NullLogger<PredecessorLineageResolver>.Instance);
        var acceptanceService = new ArtifactAcceptanceService(db, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);
        var qualityEvaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var qualityGuardPolicy = new IdentityQualityGuardPolicy();

        var orchestrator = new ImageGenerationOrchestrator(
            db, compiler, comfyService, NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider, qualityEvaluator, qualityGuardPolicy, lineageResolver, acceptanceService
        );

        // Snapshot with unsupported model
        var snapshot = CreateTestSnapshot(model: "unsupported_sdxl_base.safetensors");
        var turnId = snapshot.TurnId;
        var charId = snapshot.CharacterId;
        var genRequestId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var payload = new SceneImageGenerationOutboxPayload(turnId, charId, Guid.NewGuid(), snapshot, genRequestId);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<GpuNonTransientException>(() =>
            orchestrator.OrchestrateSceneImageGenerationAsync(payload, outboxId, "worker-1", DateTime.UtcNow));

        Assert.Contains("Terminal generation failure", ex.Message);

        // Critical assertion: ComfyUI was NEVER submitted to
        Assert.Equal(0, mockClient.QueuePromptCallCount);
        Assert.Equal(0, mockInput.UploadCallCount);
    }

    // =========================================================================
    // Test 5: All 3 Existing Builders Accept Currently Supported Combinations
    // =========================================================================
    [Fact]
    public void Test5_AllCurrentBuilders_AcceptTheirSupportedCombinations_AndRejectCrossMismatches()
    {
        var v1Builder = new VisualIdentityWorkflowV1Builder();
        var v2Builder = new VisualContinuityWorkflowV2Builder();
        var t2iBuilder = new TextToImageWorkflowV1Builder();
        const string baselineModel = "meinamix_meinaV11.safetensors";

        var viCap = new ImageGenerationCapability(baselineModel, "VisualIdentity", 1);
        var vcCap = new ImageGenerationCapability(baselineModel, "VisualContinuity", 2);
        var t2iCap = new ImageGenerationCapability(baselineModel, "TextToImage", 1);

        // Supported combinations
        Assert.True(v1Builder.CanHandle(viCap));
        Assert.True(v2Builder.CanHandle(vcCap));
        Assert.True(t2iBuilder.CanHandle(t2iCap));

        // Cross-workflow mismatch rejection
        Assert.False(v1Builder.CanHandle(vcCap));
        Assert.False(v1Builder.CanHandle(t2iCap));

        Assert.False(v2Builder.CanHandle(viCap));
        Assert.False(v2Builder.CanHandle(t2iCap));

        Assert.False(t2iBuilder.CanHandle(viCap));
        Assert.False(t2iBuilder.CanHandle(vcCap));

        // Custom models passed in constructor
        const string customModel = "custom_finetuned_checkpoint.safetensors";
        var customV1Builder = new VisualIdentityWorkflowV1Builder(new[] { customModel });
        var customCap = new ImageGenerationCapability(customModel, "VisualIdentity", 1);

        Assert.True(customV1Builder.CanHandle(customCap));
        // Base model was replaced in custom builder constructor
        Assert.False(customV1Builder.CanHandle(viCap));
    }

    // =========================================================================
    // Test 6: Model Does Not Alter Style (Style != Model Decoupling)
    // =========================================================================
    [Fact]
    public void Test6_ModelIndependence_ModelChangesDoNotAlterStyleTokens_PreservingOrthogonality()
    {
        var compiler = new VisualPromptCompiler();

        var realisticIdentity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "dark wavy hair",
            Eyes: "emerald green eyes",
            Style: "Realistic",
            VisualStyle: VisualStyle.Realistic
        );

        // Snapshot with Model A
        var snapshotModelA = CreateTestSnapshot("meinamix_meinaV11.safetensors", realisticIdentity);
        // Snapshot with Model B
        var snapshotModelB = CreateTestSnapshot("custom_sd15_photoreal.safetensors", realisticIdentity);

        var promptA = compiler.CompileScenePrompt(snapshotModelA);
        var promptB = compiler.CompileScenePrompt(snapshotModelB);
        var negativeA = compiler.CompileNegativePrompt(snapshotModelA);
        var negativeB = compiler.CompileNegativePrompt(snapshotModelB);

        // Prompts must be completely identical regardless of model
        Assert.Equal(promptA, promptB);
        Assert.Equal(negativeA, negativeB);

        // Style tokens are present as declared by VisualStyleDefinition.Realistic
        Assert.Contains("photorealistic, realistic, natural skin texture", promptA);
        Assert.DoesNotContain("anime", promptA);

        // Opposing negative tokens for Realistic style are present
        Assert.Contains("anime, cartoon, comic", negativeA);

        // Verify anime style independence as well
        var animeIdentity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "blonde twintails",
            Eyes: "blue eyes",
            Style: "Anime",
            VisualStyle: VisualStyle.Anime
        );

        var animeSnapshotModelA = CreateTestSnapshot("meinamix_meinaV11.safetensors", animeIdentity);
        var animeSnapshotModelB = CreateTestSnapshot("custom_sd15_photoreal.safetensors", animeIdentity);

        var animePromptA = compiler.CompileScenePrompt(animeSnapshotModelA);
        var animePromptB = compiler.CompileScenePrompt(animeSnapshotModelB);

        Assert.Equal(animePromptA, animePromptB);
        Assert.Contains("anime style, vibrant anime aesthetic", animePromptA);
        Assert.DoesNotContain("photorealistic", animePromptA);
    }
}
