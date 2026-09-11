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

public sealed class IdentityConditioningWorkflowResolutionTests
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
        string? workflow = null,
        int? workflowVersion = null,
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

        var profile = workflow == null
            ? GenerationProfile.CreateDefault(model: model)
            : GenerationProfile.CreateDefault(model: model, workflow: workflow, workflowVersion: workflowVersion ?? 1);

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
            generationProfile: profile,
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
    // Test 1: No Conditioning Required + Unspecified Workflow -> Resolves to TextToImage v1
    // =========================================================================
    [Fact]
    public void Test1_NoConditioning_UnspecifiedWorkflow_ResolvesTo_TextToImageV1()
    {
        var policy = CreateStandardPolicy();

        var request = new ImageGenerationRequest(
            Prompt: "A beautiful landscape without characters",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: null,
            IdentityConditioning: IdentityConditioningIntent.None
        );

        var resolved = request.ResolveEffectiveCapability();

        Assert.Equal("meinamix_meinaV11.safetensors", resolved.Model);
        Assert.Equal("TextToImage", resolved.Workflow);
        Assert.Equal(1, resolved.WorkflowVersion);

        // Validation succeeds because TextToImage is supported and no identity conditioning is required
        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 2: Canonical Reference Only + Unspecified Workflow -> Resolves to VisualIdentity v1
    // =========================================================================
    [Fact]
    public void Test2_CanonicalReferenceOnly_UnspecifiedWorkflow_ResolvesTo_VisualIdentityV1()
    {
        var policy = CreateStandardPolicy();
        const string canonUrl = "https://cdn.project00.ai/characters/hero_canon.png";

        var intent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            previousSceneReferenceUrl: null
        );

        var request = new ImageGenerationRequest(
            Prompt: "Hero standing in a field",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: null,
            IdentityConditioning: intent
        );

        var resolved = request.ResolveEffectiveCapability();

        Assert.Equal("meinamix_meinaV11.safetensors", resolved.Model);
        Assert.Equal("VisualIdentity", resolved.Workflow);
        Assert.Equal(1, resolved.WorkflowVersion);

        // Validation passes
        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 3: Previous Scene Only + Unspecified Workflow -> Resolves to VisualContinuity v2
    // =========================================================================
    [Fact]
    public void Test3_PreviousSceneOnly_UnspecifiedWorkflow_ResolvesTo_VisualContinuityV2()
    {
        var policy = CreateStandardPolicy();
        const string prevSceneUrl = "https://cdn.project00.ai/scenes/scene_01.png";

        var intent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: null,
            previousSceneReferenceUrl: prevSceneUrl
        );

        var request = new ImageGenerationRequest(
            Prompt: "Hero running forward from previous scene",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: null,
            IdentityConditioning: intent
        );

        var resolved = request.ResolveEffectiveCapability();

        Assert.Equal("meinamix_meinaV11.safetensors", resolved.Model);
        Assert.Equal("VisualContinuity", resolved.Workflow);
        Assert.Equal(2, resolved.WorkflowVersion);

        // Validation passes
        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 4: Both Canonical and Previous Scene + Unspecified Workflow -> Resolves to VisualContinuity v2
    // =========================================================================
    [Fact]
    public void Test4_CanonicalAndPreviousScene_UnspecifiedWorkflow_ResolvesTo_VisualContinuityV2()
    {
        var policy = CreateStandardPolicy();
        const string canonUrl = "https://cdn.project00.ai/characters/hero_canon.png";
        const string prevSceneUrl = "https://cdn.project00.ai/scenes/scene_01.png";

        var intent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            previousSceneReferenceUrl: prevSceneUrl
        );

        var request = new ImageGenerationRequest(
            Prompt: "Hero continuing in battle",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: null,
            IdentityConditioning: intent
        );

        var resolved = request.ResolveEffectiveCapability();

        // PreviousSceneReference takes precedence for continuous sequential visual fidelity
        Assert.Equal("meinamix_meinaV11.safetensors", resolved.Model);
        Assert.Equal("VisualContinuity", resolved.Workflow);
        Assert.Equal(2, resolved.WorkflowVersion);

        // Validation passes
        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 5 (CRITICAL): Explicit TextToImage + Identity Required -> Is NOT Overridden and Fails Validation
    // =========================================================================
    [Fact]
    public void Test5_ExplicitTextToImage_WithRequiredIdentity_IsNotOverridden_AndValidationRejects()
    {
        var policy = CreateStandardPolicy();
        const string canonUrl = "https://cdn.project00.ai/characters/hero_canon.png";

        var intent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            previousSceneReferenceUrl: null
        );

        // Caller explicitly chooses TextToImage
        var request = new ImageGenerationRequest(
            Prompt: "Hero rendered purely as text prompt",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "TextToImage",
            WorkflowVersion: 1,
            IdentityConditioning: intent
        );

        var resolved = request.ResolveEffectiveCapability();

        // STRICT PRECEDENCE: Caller's explicit workflow MUST NOT be silently rewritten
        Assert.Equal("TextToImage", resolved.Workflow);
        Assert.Equal(1, resolved.WorkflowVersion);

        // But capability validation catches the incompatibility fast and rejects it
        var ex = Assert.Throws<GpuNonTransientException>(() => request.ValidateCapability(policy));
        Assert.Contains("does not support identity conditioning required by this request", ex.Message);
    }

    // =========================================================================
    // Test 6: Explicit VisualIdentity + Canonical Identity -> Remains VisualIdentity v1
    // =========================================================================
    [Fact]
    public void Test6_ExplicitVisualIdentity_WithCanonicalIdentity_RemainsVisualIdentityV1()
    {
        var policy = CreateStandardPolicy();
        const string canonUrl = "https://cdn.project00.ai/characters/hero_canon.png";

        var intent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            previousSceneReferenceUrl: null
        );

        var request = new ImageGenerationRequest(
            Prompt: "Hero close-up portrait",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            IdentityConditioning: intent
        );

        var resolved = request.ResolveEffectiveCapability();

        Assert.Equal("VisualIdentity", resolved.Workflow);
        Assert.Equal(1, resolved.WorkflowVersion);

        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 7: Explicit VisualContinuity + Previous Scene -> Remains VisualContinuity v2
    // =========================================================================
    [Fact]
    public void Test7_ExplicitVisualContinuity_WithPreviousScene_RemainsVisualContinuityV2()
    {
        var policy = CreateStandardPolicy();
        const string prevSceneUrl = "https://cdn.project00.ai/scenes/scene_01.png";

        var intent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: null,
            previousSceneReferenceUrl: prevSceneUrl
        );

        var request = new ImageGenerationRequest(
            Prompt: "Hero continuation",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "VisualContinuity",
            WorkflowVersion: 2,
            IdentityConditioning: intent
        );

        var resolved = request.ResolveEffectiveCapability();

        Assert.Equal("VisualContinuity", resolved.Workflow);
        Assert.Equal(2, resolved.WorkflowVersion);

        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 7b (CRITICAL): Explicit VisualContinuity v1 MUST NOT Be Silently Overridden to v2
    // =========================================================================
    [Fact]
    public void Test7b_ExplicitVisualContinuityV1_IsNotOverridden_AndFailsValidationWhenUnsupported()
    {
        var policy = CreateStandardPolicy();
        const string prevSceneUrl = "https://cdn.project00.ai/scenes/scene_01.png";

        var intent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: null,
            previousSceneReferenceUrl: prevSceneUrl
        );

        // Caller explicitly selects VisualContinuity version 1 (mismatch: system only supports v2)
        var request = new ImageGenerationRequest(
            Prompt: "Hero continuation with explicit v1",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "VisualContinuity",
            WorkflowVersion: 1,
            IdentityConditioning: intent
        );

        var resolved = request.ResolveEffectiveCapability();

        // NO SILENT OVERRIDE: Must remain exactly VisualContinuity v1!
        Assert.Equal("VisualContinuity", resolved.Workflow);
        Assert.Equal(1, resolved.WorkflowVersion);

        // Capability policy does not support VisualContinuity v1, so validation must cleanly reject
        var ex = Assert.Throws<GpuNonTransientException>(() => request.ValidateCapability(policy));
        Assert.Contains("is not supported", ex.Message);
        Assert.Contains("VisualContinuity", ex.Message);
    }

    // =========================================================================
    // Test 7c (CRITICAL): Explicit VisualIdentity + Previous Scene -> Remains VisualIdentity v1
    // =========================================================================
    [Fact]
    public void Test7c_ExplicitVisualIdentity_WithPreviousScene_RemainsVisualIdentityV1_PreservingCallerIntent()
    {
        var policy = CreateStandardPolicy();
        const string canonUrl = "https://cdn.project00.ai/characters/hero_canon.png";
        const string prevSceneUrl = "https://cdn.project00.ai/scenes/scene_01.png";

        var intent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            previousSceneReferenceUrl: prevSceneUrl
        );

        // Caller deliberately wants VisualIdentity even though PreviousScene is present
        var request = new ImageGenerationRequest(
            Prompt: "Hero portrait despite having previous scene",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            IdentityConditioning: intent
        );

        var resolved = request.ResolveEffectiveCapability();

        // Must NOT be overridden to VisualContinuity v2!
        Assert.Equal("VisualIdentity", resolved.Workflow);
        Assert.Equal(1, resolved.WorkflowVersion);

        // VisualIdentity v1 supports identity conditioning, so validation passes
        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 8: IdentityConditioning.None + Explicit TextToImage -> Remains TextToImage v1 and Passes
    // =========================================================================
    [Fact]
    public void Test8_NoConditioning_ExplicitTextToImage_RemainsTextToImageV1_AndAccepted()
    {
        var policy = CreateStandardPolicy();

        var request = new ImageGenerationRequest(
            Prompt: "Landscape without identity",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "TextToImage",
            WorkflowVersion: 1,
            IdentityConditioning: IdentityConditioningIntent.None
        );

        var resolved = request.ResolveEffectiveCapability();

        Assert.Equal("TextToImage", resolved.Workflow);
        Assert.Equal(1, resolved.WorkflowVersion);

        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 9: Snapshot with Unspecified Workflow Resolves Authoritatively from Conditioning Intent
    // =========================================================================
    [Fact]
    public void Test9_SnapshotWithUnspecifiedWorkflow_ResolvesFromAuthoritativeConditioningIntent()
    {
        var policy = CreateStandardPolicy();
        const string canonUrl = "https://cdn.project00.ai/characters/authoritative_canon.png";
        const string prevSceneUrl = "https://cdn.project00.ai/scenes/authoritative_prev.png";

        var authoritativeIntent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            previousSceneReferenceUrl: prevSceneUrl,
            preservationStrength: 0.88f
        );

        // Snapshot created with unspecified workflow (workflow: null)
        var snapshot = CreateTestSnapshot(
            workflow: null,
            canonicalReferenceUrl: canonUrl,
            previousSceneImageUrl: prevSceneUrl,
            identityConditioning: authoritativeIntent
        );

        Assert.Null(snapshot.GenerationProfile.Workflow);

        var request = ImageGenerationRequest.FromSnapshot(snapshot, "Scene continuation prompt");

        // Request preserves unspecified workflow from snapshot
        Assert.Null(request.Workflow);
        Assert.Same(authoritativeIntent, request.IdentityConditioning);
        Assert.Same(authoritativeIntent, request.EffectiveIdentityConditioning);

        // Resolution produces VisualContinuity v2 because previous scene is present
        var resolved = request.ResolveEffectiveCapability();
        Assert.Equal("VisualContinuity", resolved.Workflow);
        Assert.Equal(2, resolved.WorkflowVersion);

        // Validates successfully against capability policy
        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 9b: Snapshot with Explicit Workflow Preserves Exact Workflow Even with Previous Scene
    // =========================================================================
    [Fact]
    public void Test9b_SnapshotWithExplicitWorkflow_IsNotMutated_PreservingCallerIntent()
    {
        var policy = CreateStandardPolicy();
        const string canonUrl = "https://cdn.project00.ai/characters/explicit_canon.png";
        const string prevSceneUrl = "https://cdn.project00.ai/scenes/explicit_prev.png";

        var authoritativeIntent = IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: canonUrl,
            previousSceneReferenceUrl: prevSceneUrl
        );

        // Snapshot explicitly configured for VisualIdentity despite having previous scene
        var snapshot = CreateTestSnapshot(
            workflow: "VisualIdentity",
            workflowVersion: 1,
            canonicalReferenceUrl: canonUrl,
            previousSceneImageUrl: prevSceneUrl,
            identityConditioning: authoritativeIntent
        );

        Assert.Equal("VisualIdentity", snapshot.GenerationProfile.Workflow);

        var request = ImageGenerationRequest.FromSnapshot(snapshot, "Prompt with explicit workflow snapshot");

        // FromSnapshot preserves the snapshot's explicit workflow without mutating it
        Assert.Equal("VisualIdentity", request.Workflow);
        Assert.Equal(1, request.WorkflowVersion);

        // ResolveEffectiveCapability honors caller choice: remains VisualIdentity v1!
        var resolved = request.ResolveEffectiveCapability();
        Assert.Equal("VisualIdentity", resolved.Workflow);
        Assert.Equal(1, resolved.WorkflowVersion);

        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 10: Legacy Request (Canonical Reference Only, Unspecified Workflow) -> Resolves to VisualIdentity v1
    // =========================================================================
    [Fact]
    public void Test10_LegacyRequest_CanonicalReferenceOnly_ResolvesTo_VisualIdentityV1()
    {
        var policy = CreateStandardPolicy();
        const string canonUrl = "https://cdn.project00.ai/legacy/canon.png";

        // Legacy request constructed without explicit IdentityConditioning and without Workflow
        var request = new ImageGenerationRequest(
            Prompt: "Legacy character prompt",
            Model: "meinamix_meinaV11.safetensors",
            ReferenceImageUrl: canonUrl,
            PreviousSceneImageUrl: null,
            Workflow: null,
            IdentityConditioning: null
        );

        // Effective identity conditioning derives from ReferenceImageUrl
        Assert.True(request.EffectiveIdentityConditioning.IsRequired);
        Assert.True(request.EffectiveIdentityConditioning.HasCanonicalReference);
        Assert.False(request.EffectiveIdentityConditioning.HasPreviousSceneReference);

        // Resolves to VisualIdentity v1
        var resolved = request.ResolveEffectiveCapability();
        Assert.Equal("VisualIdentity", resolved.Workflow);
        Assert.Equal(1, resolved.WorkflowVersion);

        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 11: Legacy Request (Previous Scene Only, Unspecified Workflow) -> Resolves to VisualContinuity v2
    // =========================================================================
    [Fact]
    public void Test11_LegacyRequest_PreviousSceneOnly_ResolvesTo_VisualContinuityV2()
    {
        var policy = CreateStandardPolicy();
        const string prevUrl = "https://cdn.project00.ai/legacy/prev.png";

        // Legacy request constructed without explicit IdentityConditioning and without Workflow
        var request = new ImageGenerationRequest(
            Prompt: "Legacy continuation prompt",
            Model: "meinamix_meinaV11.safetensors",
            ReferenceImageUrl: null,
            PreviousSceneImageUrl: prevUrl,
            Workflow: null,
            IdentityConditioning: null
        );

        // Effective identity conditioning derives from PreviousSceneImageUrl
        Assert.True(request.EffectiveIdentityConditioning.IsRequired);
        Assert.False(request.EffectiveIdentityConditioning.HasCanonicalReference);
        Assert.True(request.EffectiveIdentityConditioning.HasPreviousSceneReference);

        // Resolves to VisualContinuity v2
        var resolved = request.ResolveEffectiveCapability();
        Assert.Equal("VisualContinuity", resolved.Workflow);
        Assert.Equal(2, resolved.WorkflowVersion);

        request.ValidateCapability(policy);
    }

    // =========================================================================
    // Test 12: Unsupported Explicit Capability Fails Fast Before Provider (Spy Call Count == 0)
    // =========================================================================
    [Fact]
    public async Task Test12_UnsupportedExplicitCapability_FailsFastBeforeProvider_SpyProviderCountZero()
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

        // Snapshot explicitly configured for TextToImage workflow but requiring identity conditioning
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

        // Provider was NEVER invoked
        Assert.Equal(0, spyProviderService.GenerateCallCount);
        Assert.Equal(0, spyProviderService.GenerateWithResultCallCount);
    }

    // =========================================================================
    // Test 13: PR63 Capability Regressions Remain Green
    // =========================================================================
    [Fact]
    public void Test13_PR63CapabilityRegressions_RemainGreen()
    {
        var policy = CreateStandardPolicy();

        // 1. VisualIdentity with no references maps to TextToImage v1
        var emptyRefRequest = new ImageGenerationRequest(
            Prompt: "Solo scenery",
            Model: "meinamix_meinaV11.safetensors",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            ReferenceImageUrl: null,
            IdentityConditioning: IdentityConditioningIntent.None
        );

        var cap = emptyRefRequest.ResolveEffectiveCapability();
        Assert.Equal("TextToImage", cap.Workflow);
        Assert.Equal(1, cap.WorkflowVersion);
        emptyRefRequest.ValidateCapability(policy);

        // 2. Unsupported model throws GpuNonTransientException
        var badModelRequest = new ImageGenerationRequest(
            Prompt: "Unsupported model",
            Model: "flux_unknown_model.safetensors",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            ReferenceImageUrl: "https://cdn.project00.ai/ref.png"
        );

        var ex = Assert.Throws<GpuNonTransientException>(() => badModelRequest.ValidateCapability(policy));
        Assert.Contains("is not supported", ex.Message);
    }

    // =========================================================================
    // Test 14: PR64 Identity Conditioning Regressions Remain Green
    // =========================================================================
    [Fact]
    public void Test14_PR64IdentityConditioningRegressions_RemainGreen()
    {
        // 1. Canonical only
        var intent1 = IdentityConditioningIntent.FromReferences("https://cdn.project00.ai/c.png", null);
        Assert.True(intent1.IsRequired);
        Assert.True(intent1.HasCanonicalReference);
        Assert.False(intent1.HasPreviousSceneReference);

        // 2. Previous only
        var intent2 = IdentityConditioningIntent.FromReferences(null, "https://cdn.project00.ai/p.png");
        Assert.True(intent2.IsRequired);
        Assert.False(intent2.HasCanonicalReference);
        Assert.True(intent2.HasPreviousSceneReference);

        // 3. Both
        var intent3 = IdentityConditioningIntent.FromReferences("https://cdn.project00.ai/c.png", "https://cdn.project00.ai/p.png");
        Assert.True(intent3.IsRequired);
        Assert.True(intent3.HasCanonicalReference);
        Assert.True(intent3.HasPreviousSceneReference);

        // 4. Neither
        var intent4 = IdentityConditioningIntent.FromReferences(null, null);
        Assert.False(intent4.IsRequired);
        Assert.False(intent4.HasCanonicalReference);
        Assert.False(intent4.HasPreviousSceneReference);
    }

    // =========================================================================
    // Test 15: Architecture Boundary Audit (Zero Infrastructure Leaks in Domain/Application)
    // =========================================================================
    [Fact]
    public void Test15_ArchitectureBoundary_DomainAndApplication_ContainZeroInfrastructureLeaks()
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

        // Check Application interface
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
