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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GenerationProduction;

/// <summary>
/// PR #64: Comprehensive tests proving Model-Agnostic Identity Conditioning architecture.
/// Proves that IdentityConditioningIntent represents semantic intent independently from
/// SD1.5, IP-Adapter, CLIP Vision, ComfyUI, and checkpoint filenames.
/// </summary>
public sealed class IdentityConditioningTests
{
    private sealed class SpyImageGenerationProviderService : IImageGenerationService
    {
        public int GenerateWithResultCallCount { get; private set; }
        public ImageGenerationRequest? LastRequest { get; private set; }

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
            => Task.FromResult("https://cdn.project00.ai/rendered/spy.png");

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => Task.FromResult("https://cdn.project00.ai/rendered/spy.png");

        public Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            GenerateWithResultCallCount++;
            LastRequest = request;
            return Task.FromResult(new ImageGenerationResult("https://cdn.project00.ai/rendered/spy.png", "SpyProvider", "job-1", 100, 42));
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
        string? canonicalReferenceUrl = "https://cdn.project00.ai/characters/char1_canonical.png",
        string? previousSceneImageUrl = null,
        Slot2Context context = Slot2Context.ColdStart)
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
            sceneRevision: previousSceneImageUrl != null ? 2 : 1,
            visualIdentity: visualIdentity,
            sceneState: new SessionSceneState(
                CurrentLocation: "Garden",
                CurrentPosition: "Center",
                CurrentOutfit: "Dress",
                CurrentTimeOfDay: "Day",
                HeldItems: "Rose",
                Atmosphere: "Peaceful",
                SceneRevision: previousSceneImageUrl != null ? 2 : 1,
                LastUpdatedAt: DateTime.UtcNow
            ),
            transientState: new TransientVisualState(
                Expression: "smiling",
                Gaze: "looking at viewer",
                Pose: "standing"
            ),
            generationProfile: GenerationProfile.CreateDefault(model: model, workflow: "VisualIdentity", workflowVersion: 1),
            previousSceneImageUrl: previousSceneImageUrl,
            fallbackReferenceUrl: canonicalReferenceUrl,
            slot2Context: context
        );
    }

    // =========================================================================
    // Test 1: Identity Intent is Model-Independent
    // =========================================================================
    [Fact]
    public void Test1_IdentityIntent_IsModelIndependent_AcrossDifferentGenerationModels()
    {
        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "raven black hair",
            Eyes: "sapphire blue eyes",
            CanonicalReferenceUrl: "https://cdn.project00.ai/characters/aeloria_canon.png"
        );

        // Derive intent from the character identity
        var intent = identity.CreateConditioningIntent(
            preservationStrength: 0.75f,
            context: Slot2Context.ColdStart
        );

        // Create snapshot with Model A (SD1.5 baseline)
        var snapshotModelA = CreateTestSnapshot(
            model: "meinamix_meinaV11.safetensors",
            canonicalReferenceUrl: identity.CanonicalReferenceUrl
        );

        // Create snapshot with Model B (e.g. SDXL or any hypothetical checkpoint)
        var snapshotModelB = CreateTestSnapshot(
            model: "sdxl_base_1.0.safetensors",
            canonicalReferenceUrl: identity.CanonicalReferenceUrl
        );

        // Assert: Identity intent is identical regardless of model
        Assert.NotNull(snapshotModelA.IdentityConditioning);
        Assert.NotNull(snapshotModelB.IdentityConditioning);
        Assert.Equal(snapshotModelA.IdentityConditioning.IsRequired, snapshotModelB.IdentityConditioning.IsRequired);
        Assert.Equal(snapshotModelA.IdentityConditioning.CanonicalReferenceUrl, snapshotModelB.IdentityConditioning.CanonicalReferenceUrl);
        Assert.Equal(snapshotModelA.IdentityConditioning.PreviousSceneReferenceUrl, snapshotModelB.IdentityConditioning.PreviousSceneReferenceUrl);
        Assert.Equal(snapshotModelA.IdentityConditioning.Context, snapshotModelB.IdentityConditioning.Context);
        Assert.Equal(snapshotModelA.IdentityConditioning, snapshotModelB.IdentityConditioning);

        // Map both snapshots to ImageGenerationRequest
        var requestModelA = ImageGenerationRequest.FromSnapshot(snapshotModelA, "prompt A");
        var requestModelB = ImageGenerationRequest.FromSnapshot(snapshotModelB, "prompt B");

        Assert.Equal(requestModelA.EffectiveIdentityConditioning, requestModelB.EffectiveIdentityConditioning);
        Assert.Equal(identity.CanonicalReferenceUrl, requestModelA.EffectiveIdentityConditioning.CanonicalReferenceUrl);
        Assert.Equal(identity.CanonicalReferenceUrl, requestModelB.EffectiveIdentityConditioning.CanonicalReferenceUrl);
    }

    // =========================================================================
    // Test 2: Character Identity Produces Identity-Conditioning Intent
    // =========================================================================
    [Fact]
    public void Test2_CharacterIdentity_WithCanonicalReference_ProducesActiveConditioningIntent()
    {
        const string canonUrl = "https://cdn.project00.ai/characters/heroine_face.png";
        var identityWithRef = new CharacterVisualIdentity(
            Hair: "golden blonde",
            Eyes: "emerald green",
            CanonicalReferenceUrl: canonUrl
        );

        var intentWithRef = identityWithRef.CreateConditioningIntent();
        Assert.True(intentWithRef.IsRequired);
        Assert.Equal(canonUrl, intentWithRef.CanonicalReferenceUrl);
        Assert.True(intentWithRef.HasReferences);

        // Character without reference image produces inactive intent
        var identityWithoutRef = new CharacterVisualIdentity(
            Hair: "golden blonde",
            Eyes: "emerald green",
            CanonicalReferenceUrl: null,
            FullBodyUrl: null
        );

        var intentWithoutRef = identityWithoutRef.CreateConditioningIntent();
        Assert.False(intentWithoutRef.IsRequired);
        Assert.Null(intentWithoutRef.CanonicalReferenceUrl);
        Assert.False(intentWithoutRef.HasReferences);
    }

    // =========================================================================
    // Test 3: Previous Scene Reference is Preserved
    // =========================================================================
    [Fact]
    public void Test3_SameSceneGeneration_PreservesPreviousSceneReferenceInIntent()
    {
        const string canonUrl = "https://cdn.project00.ai/characters/hero_canon.png";
        const string prevSceneUrl = "https://cdn.project00.ai/scenes/session1_turn1_output.png";

        var snapshot = CreateTestSnapshot(
            canonicalReferenceUrl: canonUrl,
            previousSceneImageUrl: prevSceneUrl,
            context: Slot2Context.SameScene
        );

        Assert.NotNull(snapshot.IdentityConditioning);
        Assert.True(snapshot.IdentityConditioning.IsRequired);
        Assert.Equal(canonUrl, snapshot.IdentityConditioning.CanonicalReferenceUrl);
        Assert.Equal(prevSceneUrl, snapshot.IdentityConditioning.PreviousSceneReferenceUrl);
        Assert.Equal(Slot2Context.SameScene, snapshot.IdentityConditioning.Context);

        // Map to ImageGenerationRequest and verify intent propagation
        var request = ImageGenerationRequest.FromSnapshot(snapshot, "scene prompt", previousSceneImageUrlOverride: prevSceneUrl);
        Assert.NotNull(request.EffectiveIdentityConditioning);
        Assert.Equal(canonUrl, request.EffectiveIdentityConditioning.CanonicalReferenceUrl);
        Assert.Equal(prevSceneUrl, request.EffectiveIdentityConditioning.PreviousSceneReferenceUrl);
        Assert.Equal(Slot2Context.SameScene, request.EffectiveIdentityConditioning.Context);
    }

    // =========================================================================
    // Test 4: SD1.5 Builders Still Map Identity Intent to IP-Adapter Correctly
    // =========================================================================
    [Fact]
    public void Test4_SD15WorkflowBuilders_MapIdentityIntent_ToExpectedComfyUIGraphNodes()
    {
        IComfyUIWorkflowBuilder v1Builder = new VisualIdentityWorkflowV1Builder();
        IComfyUIWorkflowBuilder v2Builder = new VisualContinuityWorkflowV2Builder();

        const string canonUrl = "https://cdn.project00.ai/canon.png";
        const string prevUrl = "https://cdn.project00.ai/prev.png";

        var intent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            previousSceneReferenceUrl: prevUrl,
            preservationStrength: 0.70f,
            context: Slot2Context.SameScene,
            continuityMode: Slot2ConditioningMode.SceneStyleContinuity
        );

        var requestV1 = new ImageGenerationRequest(
            Prompt: "solo character",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            Seed: 12345,
            ReferenceImageUrl: canonUrl,
            IdentityConditioning: intent
        );

        var graphV1 = v1Builder.BuildWorkflow(requestV1, "uploaded_canon.png", null);

        // Verify V1 graph topology: Node 8 (IPAdapterModelLoader), Node 2 (CLIPVisionLoader), Node 10 (IPAdapterAdvanced)
        Assert.True(graphV1.ContainsKey("1"));
        Assert.True(graphV1.ContainsKey("2"));
        Assert.True(graphV1.ContainsKey("8"));
        Assert.True(graphV1.ContainsKey("10"));

        var ipModelNode = (Dictionary<string, object>)graphV1["8"];
        var ipModelInputs = (Dictionary<string, object>)ipModelNode["inputs"];
        Assert.Equal("ip-adapter-plus_sd15.safetensors", ipModelInputs["ipadapter_file"]);

        var clipNode = (Dictionary<string, object>)graphV1["2"];
        var clipInputs = (Dictionary<string, object>)clipNode["inputs"];
        Assert.Equal("CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors", clipInputs["clip_name"]);

        var ipAdvancedNode = (Dictionary<string, object>)graphV1["10"];
        var ipAdvancedInputs = (Dictionary<string, object>)ipAdvancedNode["inputs"];
        Assert.Equal(0.70, (double)ipAdvancedInputs["weight"]);

        // Verify V2 graph topology with dual conditioning
        var requestV2 = new ImageGenerationRequest(
            Prompt: "scene continuation",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "VisualContinuity",
            WorkflowVersion: 2,
            Seed: 54321,
            ReferenceImageUrl: canonUrl,
            PreviousSceneImageUrl: prevUrl,
            SceneScale: 0.25f,
            IdentityConditioning: intent
        );

        var graphV2 = v2Builder.BuildWorkflow(requestV2, "uploaded_canon.png", "uploaded_prev.png");
        Assert.True(graphV2.ContainsKey("10")); // Slot 1 canonical
        Assert.True(graphV2.ContainsKey("14")); // Slot 2 continuity
    }

    // =========================================================================
    // Test 5: Infrastructure-Specific Implementation Does NOT Leak into Domain
    // =========================================================================
    [Fact]
    public void Test5_InfrastructureDetails_DoNotLeak_IntoDomainAbstractions()
    {
        // Inspect IdentityConditioningIntent
        var intentType = typeof(IdentityConditioningIntent);
        Assert.Equal("Domain.ValueObjects", intentType.Namespace);

        // Forbidden strings in property/method names
        var forbiddenTokens = new[]
        {
            "ipadapter", "ip_adapter", "clip", "clipvision", "clip_vision",
            "comfy", "comfyui", "checkpoint", "safetensors", "gpu", "cuda",
            "vram", "node", "provider"
        };

        var properties = intentType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            var propNameLower = prop.Name.ToLowerInvariant();
            foreach (var forbidden in forbiddenTokens)
            {
                Assert.DoesNotContain(forbidden, propNameLower);
            }
        }

        // Inspect CharacterVisualIdentity
        var charIdentityType = typeof(CharacterVisualIdentity);
        var charProps = charIdentityType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in charProps)
        {
            var propNameLower = prop.Name.ToLowerInvariant();
            foreach (var forbidden in forbiddenTokens)
            {
                Assert.DoesNotContain(forbidden, propNameLower);
            }
        }
    }

    // =========================================================================
    // Test 6: Existing Generation Pipeline Remains Intact
    // =========================================================================
    [Fact]
    public async Task Test6_GenerationPipeline_FlowsCleanlyThroughOrchestrator_WithIdentityIntent()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        using var db = CreateSqliteDbContext(connection);

        var spyProvider = new SpyImageGenerationProviderService();
        var compiler = new VisualPromptCompiler();
        var dateTimeProvider = new SystemDateTimeProvider();
        var lineageResolver = new PredecessorLineageResolver(db, NullLogger<PredecessorLineageResolver>.Instance);
        var acceptanceService = new ArtifactAcceptanceService(db, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);
        var qualityEvaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var qualityGuardPolicy = new IdentityQualityGuardPolicy();

        var builders = new IComfyUIWorkflowBuilder[]
        {
            new VisualIdentityWorkflowV1Builder(),
            new VisualContinuityWorkflowV2Builder(),
            new TextToImageWorkflowV1Builder()
        };
        var policy = new WorkflowCapabilityPolicy(builders);

        var orchestrator = new ImageGenerationOrchestrator(
            db, compiler, spyProvider, NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider, qualityEvaluator, qualityGuardPolicy, lineageResolver, acceptanceService,
            capabilityPolicy: policy
        );

        const string canonUrl = "https://cdn.project00.ai/characters/seraphina_canon.png";
        var snapshot = CreateTestSnapshot(canonicalReferenceUrl: canonUrl);
        var payload = new SceneImageGenerationOutboxPayload(snapshot.TurnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, Guid.NewGuid());

        // Execute orchestrator
        var result = await orchestrator.OrchestrateSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow);

        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.Equal(1, spyProvider.GenerateWithResultCallCount);
        Assert.NotNull(spyProvider.LastRequest);
        Assert.NotNull(spyProvider.LastRequest.EffectiveIdentityConditioning);
        Assert.True(spyProvider.LastRequest.EffectiveIdentityConditioning.IsRequired);
        Assert.Equal(canonUrl, spyProvider.LastRequest.EffectiveIdentityConditioning.CanonicalReferenceUrl);
    }

    // =========================================================================
    // Test 7: Identity Snapshot Remains Deterministic
    // =========================================================================
    [Fact]
    public void Test7_IdentitySnapshot_IsDeterministic_AndPreservesValueObjectEquality()
    {
        const string canonUrl = "https://cdn.project00.ai/canon.png";
        const string prevUrl = "https://cdn.project00.ai/prev.png";

        var intent1 = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            previousSceneReferenceUrl: prevUrl,
            preservationStrength: 0.65f,
            context: Slot2Context.SameScene,
            continuityMode: Slot2ConditioningMode.SceneStyleContinuity
        );

        var intent2 = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            previousSceneReferenceUrl: prevUrl,
            preservationStrength: 0.65f,
            context: Slot2Context.SameScene,
            continuityMode: Slot2ConditioningMode.SceneStyleContinuity
        );

        Assert.Equal(intent1, intent2);
        Assert.Equal(intent1.GetHashCode(), intent2.GetHashCode());

        var diffStrengthIntent = intent1 with { PreservationStrength = 0.80f };
        Assert.NotEqual(intent1, diffStrengthIntent);

        var diffContextIntent = intent1 with { Context = Slot2Context.SceneTransition };
        Assert.NotEqual(intent1, diffContextIntent);
    }
}
