using Application.Common;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.ImageGeneration;
using Infrastructure.ImageGeneration.ComfyUI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class ModelAgnosticFoundationTests
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
                new("sample.png", "", "output")
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
        public Task<string> EnsureImageUploadedAsync(string? referenceImageUrl, CancellationToken ct = default)
            => Task.FromResult("uploaded_input.png");
    }

    [Fact]
    public void Test1_ConfiguredModel_AiProvidersDefaultModel_PropagatesToProfile_Snapshot_And_Request()
    {
        // 1. Arrange configuration with AiProviders:ImageGeneration:DefaultModel
        const string expectedModel = "custom_anime_diffusion_v2.safetensors";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProviders:ImageGeneration:DefaultModel"] = expectedModel
            })
            .Build();

        var provider = new VisualGenerationProfileProvider(config);
        var character = new Character(
            name: "Test Character",
            title: "Tester",
            avatarUrl: "https://cdn.project00.ai/avatar.png",
            personalityPrompt: "Friendly",
            greeting: "Hello",
            category: "Anime"
        );

        // 2. Act: resolve profile
        var profile = provider.ResolveProfile(character);

        // 3. Assert profile model matches configured model
        Assert.Equal(expectedModel, profile.Model);

        // 4. Create snapshot with profile
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: character.Id,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("Garden", "Center"),
            TransientState: null,
            GenerationProfile: profile
        );

        // 5. Build ImageGenerationRequest from snapshot
        var request = ImageGenerationRequest.FromSnapshot(snapshot, "a peaceful garden");

        // 6. Assert model propagated through entire pipeline without being dropped or altered
        Assert.Equal(profile.Model, request.Model);
        Assert.Equal(expectedModel, request.Model);

        // 7. Semantics assertion: Configuration alone does NOT automatically mean a workflow can execute it
        // A standard SD1.5 builder without this model declared in SupportedModels must reject it cleanly
        var defaultBuilder = new VisualIdentityWorkflowV1Builder();
        Assert.False(defaultBuilder.CanHandle(request.Workflow, request.WorkflowVersion, request.Model));
    }

    [Fact]
    public void Test1b_ConfiguredModel_ComfyUIModelName_Fallback_PropagatesCorrectly()
    {
        // Arrange configuration with AiProviders:ComfyUI:ModelName only (legacy fallback)
        const string expectedModel = "dreamshaper_v8.safetensors";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProviders:ComfyUI:ModelName"] = expectedModel
            })
            .Build();

        var provider = new VisualGenerationProfileProvider(config);
        var character = new Character(
            name: "Test Character",
            title: "Tester",
            avatarUrl: "https://cdn.project00.ai/avatar.png",
            personalityPrompt: "Friendly",
            greeting: "Hello",
            category: "Anime"
        );

        var profile = provider.ResolveProfile(character);

        Assert.Equal(expectedModel, profile.Model);
    }

    [Fact]
    public void Test1c_MissingConfiguration_UsesExpectedDefaultFallback()
    {
        // Arrange: No configuration provided
        var provider = new VisualGenerationProfileProvider();
        var character = new Character(
            name: "Test Character",
            title: "Tester",
            avatarUrl: "https://cdn.project00.ai/avatar.png",
            personalityPrompt: "Friendly",
            greeting: "Hello",
            category: "Anime"
        );

        var profile = provider.ResolveProfile(character);

        Assert.Equal(VisualGenerationProfileProvider.DefaultModelFallback, profile.Model);
        Assert.Equal(VisualGenerationProfileProvider.DefaultModelId, profile.Model);
        Assert.Equal("meinamix", profile.Model);
    }

    [Fact]
    public void Test2_ExistingSD15_WorkflowBuilders_Support_DefaultModel()
    {
        const string sd15Model = "meinamix_meinaV11.safetensors";

        var v1Builder = new VisualIdentityWorkflowV1Builder();
        var v2Builder = new VisualContinuityWorkflowV2Builder();
        var t2iBuilder = new TextToImageWorkflowV1Builder();

        // Compatibility checks
        Assert.True(v1Builder.CanHandle("VisualIdentity", 1, sd15Model));
        Assert.True(v2Builder.CanHandle("VisualContinuity", 2, sd15Model));
        Assert.True(t2iBuilder.CanHandle("TextToImage", 1, sd15Model));

        // Workflow builders successfully build prompt graphs
        var req = new ImageGenerationRequest(
            Prompt: "masterpiece, best quality, 1girl",
            Model: sd15Model,
            Seed: 42
        );

        var v1Graph = v1Builder.BuildWorkflow(req, "test_ref.png");
        Assert.NotEmpty(v1Graph);

        var v2Graph = v2Builder.BuildWorkflow(req, "test_ref.png", "test_prev.png");
        Assert.NotEmpty(v2Graph);

        var t2iGraph = t2iBuilder.BuildWorkflow(req, "dummy.png");
        Assert.NotEmpty(t2iGraph);
    }

    [Fact]
    public void Test2b_WorkflowBuilders_WhenRequestModelMissingOrWhitespace_ThrowsGpuNonTransientException()
    {
        // Assert: NO builder may have a hidden fallback to meinamix. Model must be explicitly required.
        var v1Builder = new VisualIdentityWorkflowV1Builder();
        var v2Builder = new VisualContinuityWorkflowV2Builder();
        var t2iBuilder = new TextToImageWorkflowV1Builder();

        var nullModelReq = new ImageGenerationRequest(Prompt: "1girl", Model: null, Seed: 42);
        var emptyModelReq = new ImageGenerationRequest(Prompt: "1girl", Model: "   ", Seed: 42);

        var ex1 = Assert.Throws<GpuNonTransientException>(() => v1Builder.BuildWorkflow(nullModelReq, "ref.png"));
        Assert.Contains("Model is required", ex1.Message);

        var ex2 = Assert.Throws<GpuNonTransientException>(() => v2Builder.BuildWorkflow(emptyModelReq, "ref.png", null));
        Assert.Contains("Model is required", ex2.Message);

        var ex3 = Assert.Throws<GpuNonTransientException>(() => t2iBuilder.BuildWorkflow(nullModelReq, "dummy.png"));
        Assert.Contains("Model is required", ex3.Message);
    }

    [Fact]
    public void Test2c_WorkflowBuilders_WhenRequestModelNotSupported_ThrowsGpuNonTransientException()
    {
        var v1Builder = new VisualIdentityWorkflowV1Builder();
        var v2Builder = new VisualContinuityWorkflowV2Builder();
        var t2iBuilder = new TextToImageWorkflowV1Builder();

        var unsupportedReq = new ImageGenerationRequest(Prompt: "1girl", Model: "unsupported_checkpoint.safetensors", Seed: 42);

        var ex1 = Assert.Throws<GpuNonTransientException>(() => v1Builder.BuildWorkflow(unsupportedReq, "ref.png"));
        Assert.Contains("not supported", ex1.Message);

        var ex2 = Assert.Throws<GpuNonTransientException>(() => v2Builder.BuildWorkflow(unsupportedReq, "ref.png", null));
        Assert.Contains("not supported", ex2.Message);

        var ex3 = Assert.Throws<GpuNonTransientException>(() => t2iBuilder.BuildWorkflow(unsupportedReq, "dummy.png"));
        Assert.Contains("not supported", ex3.Message);
    }

    [Fact]
    public void Test2d_WorkflowBuilders_CanBeExtendedWithSupportedModels_AndGenerateCorrectly()
    {
        // Demonstrates true capability semantics: When a workflow builder is declared to support an additional model,
        // it can handle it and generates the graph using that exact model without fallback.
        const string customModel = "custom_sd15_checkpoint.safetensors";
        var customSupported = new[] { "meinamix_meinaV11.safetensors", customModel };

        var customV1Builder = new VisualIdentityWorkflowV1Builder(customSupported);
        Assert.True(customV1Builder.CanHandle("VisualIdentity", 1, customModel));

        var req = new ImageGenerationRequest(
            Prompt: "masterpiece, 1girl",
            Model: customModel,
            Seed: 12345
        );

        var graph = customV1Builder.BuildWorkflow(req, "ref.png");
        Assert.NotEmpty(graph);

        // Verify that the checkpoint node in the graph contains the exact custom model name
        var ckptNode = (Dictionary<string, object>)graph["4"];
        var inputs = (Dictionary<string, object>)ckptNode["inputs"];
        Assert.Equal(customModel, inputs["ckpt_name"]);
    }

    [Fact]
    public async Task Test3_UnsupportedModel_FailsCleanly_WithGpuNonTransientException_BeforeQueuePrompt()
    {
        var mockClient = new MockComfyUIClient();
        var storage = new MockStorageService();
        var inputService = new MockInputImageService();
        var builders = new IComfyUIWorkflowBuilder[]
        {
            new VisualIdentityWorkflowV1Builder(),
            new VisualContinuityWorkflowV2Builder(),
            new TextToImageWorkflowV1Builder()
        };
        var config = new ConfigurationBuilder().Build();

        var service = new ComfyUIImageGenerationService(
            mockClient,
            storage,
            inputService,
            builders,
            config,
            NullLogger<ComfyUIImageGenerationService>.Instance
        );

        var unsupportedReq = new ImageGenerationRequest(
            Prompt: "masterpiece, anime girl",
            Model: "sdxl_base_1.0.safetensors",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            ReferenceImageUrl: "https://cdn.project00.ai/ref.png"
        );

        // Act & Assert
        var ex = await Assert.ThrowsAsync<GpuNonTransientException>(() =>
            service.GenerateImageWithResultAsync(unsupportedReq));

        Assert.Contains("is not compatible with model", ex.Message);
        Assert.Contains("sdxl_base_1.0.safetensors", ex.Message);

        // Crucial verification: QueuePromptAsync was NEVER called
        Assert.Equal(0, mockClient.QueuePromptCallCount);
    }

    [Fact]
    public void Test4_ModelSensitive_Fingerprint_ProducesDistinctFingerprints_ForDifferentModels()
    {
        var fingerprintService = new GenerationFingerprintService();
        var jobId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var profileA = GenerationProfile.CreateDefault(model: "model_anime_a.safetensors", seed: 42);
        var profileB = GenerationProfile.CreateDefault(model: "model_anime_b.safetensors", seed: 42);

        var snapshotA = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: charId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("Garden", "Center"),
            TransientState: null,
            GenerationProfile: profileA
        );

        var snapshotB = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: charId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("Garden", "Center"),
            TransientState: null,
            GenerationProfile: profileB
        );

        var fpA = fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshotA,
            profile: profileA,
            derivedSeed: 42L,
            attemptNumber: 1,
            workflow: "VisualIdentity",
            workflowVersion: 1,
            modelIdentifier: profileA.Model,
            compiledPrompt: "masterpiece garden"
        );

        var fpB = fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshotB,
            profile: profileB,
            derivedSeed: 42L,
            attemptNumber: 1,
            workflow: "VisualIdentity",
            workflowVersion: 1,
            modelIdentifier: profileB.Model,
            compiledPrompt: "masterpiece garden"
        );

        Assert.NotEqual(fpA, fpB);
    }

    [Fact]
    public void Test5_SeedDerivation_Regression_SplitMix64_DeterminismRemainsIntact()
    {
        const long baseSeed = 123456789L;

        // Attempt 1 must return baseSeed unmodified
        Assert.Equal(baseSeed, DeterministicSeedDerivation.Derive(baseSeed, 1));

        // Attempts > 1 must return stable deterministic SplitMix64 seeds
        var attempt2 = DeterministicSeedDerivation.Derive(baseSeed, 2);
        var attempt3 = DeterministicSeedDerivation.Derive(baseSeed, 3);
        var attempt4 = DeterministicSeedDerivation.Derive(baseSeed, 4);

        Assert.NotEqual(baseSeed, attempt2);
        Assert.NotEqual(attempt2, attempt3);
        Assert.True(attempt2 > 0);
        Assert.True(attempt3 > 0);
        Assert.True(attempt4 > 0);

        // Exact determinism check across repeated invocations
        Assert.Equal(attempt2, DeterministicSeedDerivation.Derive(baseSeed, 2));
        Assert.Equal(attempt3, DeterministicSeedDerivation.Derive(baseSeed, 3));
    }
}
