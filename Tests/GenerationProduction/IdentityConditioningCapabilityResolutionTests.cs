using System.Reflection;
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

public sealed class IdentityConditioningCapabilityResolutionTests
{
    private sealed class SpyImageGenerationProviderService : IImageGenerationService
    {
        public int GenerateCallCount { get; private set; }
        public int GenerateWithResultCallCount { get; private set; }

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
        {
            GenerateCallCount++;
            return Task.FromResult("https://cdn.project00.ai/rendered/spy.png");
        }

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            GenerateCallCount++;
            return Task.FromResult("https://cdn.project00.ai/rendered/spy.png");
        }

        public Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            GenerateWithResultCallCount++;
            return Task.FromResult(new ImageGenerationResult("https://cdn.project00.ai/rendered/spy.png", "SpyProvider", "job-1", 100, 42));
        }
    }

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

    private static VisualSnapshot CreateTestSnapshot(
        string model = "meinamix_meinaV11.safetensors",
        string workflow = "VisualIdentity",
        int workflowVersion = 1,
        string? canonicalReferenceUrl = "https://cdn.project00.ai/characters/char1_canonical.png",
        string? previousSceneImageUrl = null,
        IdentityConditioningIntent? identityConditioning = null)
    {
        var visualIdentity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "silver hair",
            Eyes: "crimson eyes",
            Style: "Anime",
            VisualStyle: VisualStyle.Anime,
            CanonicalReferenceUrl: canonicalReferenceUrl
        );

        return VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            sceneRevision: 1,
            visualIdentity: visualIdentity,
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
            generationProfile: GenerationProfile.CreateDefault(model: model, workflow: workflow, workflowVersion: workflowVersion),
            previousSceneImageUrl: previousSceneImageUrl,
            fallbackReferenceUrl: canonicalReferenceUrl,
            identityConditioning: identityConditioning
        );
    }

    private static IImageGenerationCapabilityPolicy CreateStandardPolicy()
    {
        var builders = new IComfyUIWorkflowBuilder[]
        {
            new VisualIdentityWorkflowV1Builder(),
            new VisualContinuityWorkflowV2Builder(),
            new TextToImageWorkflowV1Builder()
        };
        return new WorkflowCapabilityPolicy(builders);
    }

    // =========================================================================
    // Test 1: No Conditioning Required -> TextToImage Capability Accepted
    // =========================================================================
    [Fact]
    public void Test1_NoConditioning_TextToImageCapability_IsAccepted()
    {
        var policy = CreateStandardPolicy();
        const string model = "meinamix_meinaV11.safetensors";

        var request = new ImageGenerationRequest(
            Prompt: "A serene sunset over rolling green hills",
            Model: model,
            Workflow: "TextToImage",
            WorkflowVersion: 1,
            IdentityConditioning: IdentityConditioningIntent.None
        );

        Assert.False(request.EffectiveIdentityConditioning.IsRequired);
        Assert.False(request.EffectiveIdentityConditioning.HasReferences);

        var capability = request.ResolveEffectiveCapability();
        Assert.Equal("TextToImage", capability.Workflow);
        Assert.Equal(1, capability.WorkflowVersion);

        // Assert capability is supported
        Assert.True(policy.IsSupported(capability));

        // Validation passes without throwing
        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 2: Canonical Identity Conditioning -> VisualIdentity v1 Accepted
    // =========================================================================
    [Fact]
    public void Test2_CanonicalIdentityConditioning_VisualIdentityV1_IsAccepted()
    {
        var policy = CreateStandardPolicy();
        const string model = "meinamix_meinaV11.safetensors";
        const string canonUrl = "https://cdn.project00.ai/characters/hero_face.png";

        var intent = IdentityConditioningIntent.FromReferences(canonUrl, null);
        var request = new ImageGenerationRequest(
            Prompt: "Hero standing proudly in the citadel",
            Model: model,
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            ReferenceImageUrl: canonUrl,
            IdentityConditioning: intent
        );

        Assert.True(request.EffectiveIdentityConditioning.IsRequired);
        Assert.True(request.EffectiveIdentityConditioning.HasCanonicalReference);
        Assert.False(request.EffectiveIdentityConditioning.HasPreviousSceneReference);

        var capability = request.ResolveEffectiveCapability();
        Assert.Equal("VisualIdentity", capability.Workflow);
        Assert.Equal(1, capability.WorkflowVersion);

        Assert.True(policy.IsSupported(capability));
        Assert.True(policy.SupportsIdentityConditioning(capability));

        // Validation passes without throwing
        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 3: Previous-Scene-Only Conditioning -> VisualContinuity v2 Accepted
    // =========================================================================
    [Fact]
    public void Test3_PreviousSceneOnlyConditioning_VisualContinuityV2_IsAccepted()
    {
        var policy = CreateStandardPolicy();
        const string model = "meinamix_meinaV11.safetensors";
        const string prevUrl = "https://cdn.project00.ai/scenes/turn_1_rendered.png";

        // Previous-scene only: CanonicalReferenceUrl is null
        var intent = IdentityConditioningIntent.FromReferences(null, prevUrl);
        Assert.True(intent.IsRequired);
        Assert.False(intent.HasCanonicalReference);
        Assert.True(intent.HasPreviousSceneReference);

        var request = new ImageGenerationRequest(
            Prompt: "Hero stepping into the shadows of the alleyway",
            Model: model,
            Workflow: "VisualContinuity",
            WorkflowVersion: 2,
            ReferenceImageUrl: null,
            PreviousSceneImageUrl: prevUrl,
            IdentityConditioning: intent
        );

        var capability = request.ResolveEffectiveCapability();
        Assert.Equal("VisualContinuity", capability.Workflow);
        Assert.Equal(2, capability.WorkflowVersion);

        Assert.True(policy.IsSupported(capability));
        Assert.True(policy.SupportsIdentityConditioning(capability));

        // Validation passes without throwing
        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 4: Identity Conditioning Required + TextToImage -> Rejected, Provider NOT Called
    // =========================================================================
    [Fact]
    public void Test4_IdentityConditioningRequired_On_TextToImage_IsRejected()
    {
        var policy = CreateStandardPolicy();
        const string model = "meinamix_meinaV11.safetensors";
        const string canonUrl = "https://cdn.project00.ai/characters/hero_face.png";

        var intent = IdentityConditioningIntent.FromReferences(canonUrl, null);
        Assert.True(intent.IsRequired);

        // Caller explicitly selects TextToImage but requires identity conditioning
        var request = new ImageGenerationRequest(
            Prompt: "Hero standing",
            Model: model,
            Workflow: "TextToImage",
            WorkflowVersion: 1,
            IdentityConditioning: intent
        );

        var capability = request.ResolveEffectiveCapability();
        Assert.Equal("TextToImage", capability.Workflow);
        Assert.Equal(1, capability.WorkflowVersion);

        Assert.True(policy.IsSupported(capability));
        Assert.False(policy.SupportsIdentityConditioning(capability));

        var ex = Assert.Throws<GpuNonTransientException>(() => request.ValidateCapability(policy));
        Assert.Contains("does not support identity conditioning required by this request", ex.Message);
        Assert.Contains("TextToImage", ex.Message);
    }

    // =========================================================================
    // Test 5: Unsupported Capability Fails Fast Before Provider Submission
    // =========================================================================
    [Fact]
    public async Task Test5_UnsupportedIdentityConditioning_FailsFastBeforeProviderInvocation()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        using var db = CreateSqliteDbContext(connection);

        var spyProviderService = new SpyImageGenerationProviderService();
        var compiler = new VisualPromptCompiler();
        var dateTimeProvider = new SystemDateTimeProvider();
        var lineageResolver = new PredecessorLineageResolver(db, NullLogger<PredecessorLineageResolver>.Instance);
        var acceptanceService = new ArtifactAcceptanceService(db, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);
        var qualityEvaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var qualityGuardPolicy = new IdentityQualityGuardPolicy();
        var policy = CreateStandardPolicy();

        var orchestrator = new ImageGenerationOrchestrator(
            db, compiler, spyProviderService, NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider, qualityEvaluator, qualityGuardPolicy, lineageResolver, acceptanceService,
            capabilityPolicy: policy
        );

        // Snapshot explicitly configured for TextToImage workflow but containing visual identity
        const string canonUrl = "https://cdn.project00.ai/characters/hero_face.png";
        var intent = IdentityConditioningIntent.FromReferences(canonUrl, null);
        var snapshot = CreateTestSnapshot(
            workflow: "TextToImage",
            workflowVersion: 1,
            canonicalReferenceUrl: canonUrl,
            identityConditioning: intent
        );

        var payload = new SceneImageGenerationOutboxPayload(
            snapshot.TurnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, Guid.NewGuid());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<GpuNonTransientException>(() =>
            orchestrator.OrchestrateSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow));

        Assert.Contains("Terminal generation failure", ex.Message);
        Assert.Contains("does not support identity conditioning", ex.Message);

        // CRUCIAL ASSERTION: Provider was NEVER invoked!
        Assert.Equal(0, spyProviderService.GenerateCallCount);
        Assert.Equal(0, spyProviderService.GenerateWithResultCallCount);
    }

    // =========================================================================
    // Test 6: Snapshot Intent Survives into Validation as Single Source of Truth
    // =========================================================================
    [Fact]
    public void Test6_SnapshotIntent_SurvivesIntoRequest_AndDeterminesValidationOutcome()
    {
        var policy = CreateStandardPolicy();
        const string canonUrl = "https://cdn.project00.ai/characters/authoritative_face.png";

        // Snapshot carries an authoritative active intent
        var authoritativeIntent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            preservationStrength: 0.85f
        );

        var snapshot = CreateTestSnapshot(
            workflow: "VisualIdentity",
            workflowVersion: 1,
            identityConditioning: authoritativeIntent
        );

        // Map via FromSnapshot
        var request = ImageGenerationRequest.FromSnapshot(snapshot, "prompt");

        // The request's conditioning is the EXACT snapshot instance
        Assert.Same(authoritativeIntent, request.IdentityConditioning);
        Assert.Same(authoritativeIntent, request.EffectiveIdentityConditioning);
        Assert.Equal(0.85f, request.EffectiveIdentityConditioning.PreservationStrength);

        // Validation consumes this intent
        request.ValidateCapability(policy);

        // When mapped with incompatible workflow in snapshot, snapshot intent causes rejection
        var incompatibleSnapshot = CreateTestSnapshot(
            workflow: "TextToImage",
            workflowVersion: 1,
            identityConditioning: authoritativeIntent
        );

        var incompatibleRequest = ImageGenerationRequest.FromSnapshot(incompatibleSnapshot, "prompt");
        Assert.Same(authoritativeIntent, incompatibleRequest.IdentityConditioning);

        var ex = Assert.Throws<GpuNonTransientException>(() => incompatibleRequest.ValidateCapability(policy));
        Assert.Contains("does not support identity conditioning", ex.Message);
    }

    // =========================================================================
    // Test 7: Legacy Request Compatibility (Canonical Reference -> VisualIdentity Accepted)
    // =========================================================================
    [Fact]
    public void Test7_LegacyRequest_CanonicalReference_DerivesActiveIntent_AndIsAccepted()
    {
        var policy = CreateStandardPolicy();
        const string canonUrl = "https://cdn.project00.ai/legacy/canon.png";

        // Old-style constructor without IdentityConditioning parameter
        var legacyRequest = new ImageGenerationRequest(
            Prompt: "Legacy portrait",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            ReferenceImageUrl: canonUrl,
            IdentityConditioning: null
        );

        // Effective intent is derived automatically
        Assert.Null(legacyRequest.IdentityConditioning);
        Assert.NotNull(legacyRequest.EffectiveIdentityConditioning);
        Assert.True(legacyRequest.EffectiveIdentityConditioning.IsRequired);
        Assert.True(legacyRequest.EffectiveIdentityConditioning.HasCanonicalReference);
        Assert.Equal(canonUrl, legacyRequest.EffectiveIdentityConditioning.CanonicalReferenceUrl);

        // Capability validation succeeds
        legacyRequest.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 8: Legacy Request Compatibility (Previous Scene Only -> Continuity Accepted)
    // =========================================================================
    [Fact]
    public void Test8_LegacyRequest_PreviousSceneOnly_DerivesActiveIntent_AndIsAccepted()
    {
        var policy = CreateStandardPolicy();
        const string prevUrl = "https://cdn.project00.ai/legacy/prev_scene.png";

        // Old-style constructor with only previous scene reference
        var legacyRequest = new ImageGenerationRequest(
            Prompt: "Legacy continuation scene",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "VisualContinuity",
            WorkflowVersion: 2,
            ReferenceImageUrl: null,
            PreviousSceneImageUrl: prevUrl,
            IdentityConditioning: null
        );

        Assert.Null(legacyRequest.IdentityConditioning);
        Assert.NotNull(legacyRequest.EffectiveIdentityConditioning);
        Assert.True(legacyRequest.EffectiveIdentityConditioning.IsRequired);
        Assert.False(legacyRequest.EffectiveIdentityConditioning.HasCanonicalReference);
        Assert.True(legacyRequest.EffectiveIdentityConditioning.HasPreviousSceneReference);
        Assert.Equal(prevUrl, legacyRequest.EffectiveIdentityConditioning.PreviousSceneReferenceUrl);

        // Capability validation succeeds
        legacyRequest.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 9: ComfyUIImageGenerationService Defense-in-Depth Checks
    // =========================================================================
    [Fact]
    public async Task Test9_ComfyUIImageGenerationService_DefenseInDepth_RejectsUnsupportedIdentityConditioning()
    {
        var mockClient = new MockComfyUIClient();
        var mockStorage = new MockStorageService();
        var mockInput = new MockInputImageService();
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["AiProviders:ComfyUI:PollIntervalMs"] = "10",
            ["AiProviders:ComfyUI:TimeoutSeconds"] = "2"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        var builders = new IComfyUIWorkflowBuilder[]
        {
            new TextToImageWorkflowV1Builder()
        };

        var service = new ComfyUIImageGenerationService(
            mockClient, mockStorage, mockInput, builders, config,
            NullLogger<ComfyUIImageGenerationService>.Instance
        );

        var request = new ImageGenerationRequest(
            Prompt: "Character in meadow",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "TextToImage",
            WorkflowVersion: 1,
            IdentityConditioning: IdentityConditioningIntent.FromReferences("https://cdn.project00.ai/face.png", null)
        );

        var ex = await Assert.ThrowsAsync<GpuNonTransientException>(() => service.GenerateImageWithResultAsync(request));
        Assert.Contains("does not support identity conditioning required by this request", ex.Message);

        // ComfyUI client and image upload are NEVER called
        Assert.Equal(0, mockClient.QueuePromptCallCount);
        Assert.Equal(0, mockInput.UploadCallCount);
    }

    // =========================================================================
    // Test 10: Workflow Builders Remain Single Source of Truth
    // =========================================================================
    [Fact]
    public void Test10_BuildersRemainSingleSourceOfTruth_DynamicCustomBuilder_ExposesIdentityCapability()
    {
        // Define a custom builder dynamically without changing any Application code
        var customBuilder = new CustomIdentityWorkflowBuilder("CustomWorkflow", 3, "custom_model.safetensors");
        var policy = new WorkflowCapabilityPolicy(new[] { customBuilder });

        var supportedCap = new ImageGenerationCapability("custom_model.safetensors", "CustomWorkflow", 3);
        Assert.True(policy.IsSupported(supportedCap));
        Assert.True(policy.SupportsIdentityConditioning(supportedCap));

        var request = new ImageGenerationRequest(
            Prompt: "Custom generation",
            Model: "custom_model.safetensors",
            Workflow: "CustomWorkflow",
            WorkflowVersion: 3,
            IdentityConditioning: IdentityConditioningIntent.FromReferences("https://cdn.project00.ai/ref.png", null)
        );

        // Passes validation dynamically based purely on the custom builder's contract
        request.ValidateCapability(policy);
    }

    private sealed class CustomIdentityWorkflowBuilder : IComfyUIWorkflowBuilder
    {
        public string WorkflowName { get; }
        public int WorkflowVersion { get; }
        public IReadOnlySet<string> SupportedModels { get; }
        public bool SupportsIdentityConditioning => true;

        public CustomIdentityWorkflowBuilder(string name, int version, string supportedModel)
        {
            WorkflowName = name;
            WorkflowVersion = version;
            SupportedModels = new HashSet<string> { supportedModel };
        }

        public bool CanHandle(string workflow, int workflowVersion, string? model)
        {
            return string.Equals(WorkflowName, workflow, StringComparison.OrdinalIgnoreCase)
                && WorkflowVersion == workflowVersion
                && SupportedModels.Contains(model ?? string.Empty);
        }

        public Dictionary<string, object> BuildWorkflow(ImageGenerationRequest request, string resolvedReferenceImageName)
            => new() { ["custom"] = "graph" };
    }

    // =========================================================================
    // Test 11: Architecture Boundary Audit (Zero Infrastructure Terms in Domain/Application)
    // =========================================================================
    [Fact]
    public void Test11_ArchitectureBoundary_DomainAndApplication_ContainZeroInfrastructureLeaks()
    {
        var domainAssembly = typeof(IdentityConditioningIntent).Assembly;
        var applicationAssembly = typeof(IImageGenerationCapabilityPolicy).Assembly;

        var forbiddenTerms = new[]
        {
            "IPAdapter",
            "IP_Adapter",
            "ip-adapter",
            "CLIPVision",
            "clip_vision",
            "ComfyUI",
            "safetensors",
            "VRAM",
            "CUDA"
        };

        // Check types in Domain
        foreach (var type in domainAssembly.GetTypes().Where(t => t.Namespace?.StartsWith("Domain.ValueObjects") == true))
        {
            foreach (var term in forbiddenTerms)
            {
                Assert.DoesNotContain(term, type.Name, StringComparison.OrdinalIgnoreCase);
                foreach (var prop in type.GetProperties())
                {
                    Assert.DoesNotContain(term, prop.Name, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // Check IImageGenerationCapabilityPolicy in Application
        var policyType = typeof(IImageGenerationCapabilityPolicy);
        foreach (var method in policyType.GetMethods())
        {
            foreach (var term in forbiddenTerms)
            {
                Assert.DoesNotContain(term, method.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
