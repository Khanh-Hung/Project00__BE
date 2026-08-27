using Application.Services;
using Domain.ValueObjects;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class GenerationFingerprintTests
{
    private readonly GenerationFingerprintService _fingerprintService = new();

    [Fact]
    public void ComputeFingerprint_SameInputs_ProducesIdenticalFingerprint()
    {
        var jobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var turnId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var sessionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var characterId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        var snapshot1 = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene-forest", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var snapshot2 = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene-forest", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var fp1 = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot1,
            profile: snapshot1.GenerationProfile,
            derivedSeed: 123456789L,
            attemptNumber: 1,
            workflow: "VisualIdentity",
            workflowVersion: 1,
            modelIdentifier: "ComfyUI/SDXL",
            compiledPrompt: "a knight standing in the forest",
            compiledNegativePrompt: "blurry, low quality",
            previousReferenceUrl: null,
            mitigationAction: "Pass"
        );

        var fp2 = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot2,
            profile: snapshot2.GenerationProfile,
            derivedSeed: 123456789L,
            attemptNumber: 1,
            workflow: "VisualIdentity",
            workflowVersion: 1,
            modelIdentifier: "ComfyUI/SDXL",
            compiledPrompt: "a knight standing in the forest",
            compiledNegativePrompt: "blurry, low quality",
            previousReferenceUrl: null,
            mitigationAction: "Pass"
        );

        Assert.Equal(fp1, fp2);
        Assert.False(string.IsNullOrWhiteSpace(fp1));
        Assert.Equal(64, fp1.Length); // SHA-256 hex string length
    }

    [Fact]
    public void ComputeFingerprint_DifferentSeed_ProducesDifferentFingerprint()
    {
        var jobId = Guid.NewGuid();
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var fp1 = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot,
            profile: snapshot.GenerationProfile,
            derivedSeed: 100L,
            attemptNumber: 1
        );

        var fp2 = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot,
            profile: snapshot.GenerationProfile,
            derivedSeed: 200L,
            attemptNumber: 1
        );

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void ComputeFingerprint_DifferentModelIdentifier_ProducesDifferentFingerprint()
    {
        var jobId = Guid.NewGuid();
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var fpSdxl = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot,
            profile: snapshot.GenerationProfile,
            derivedSeed: 100L,
            attemptNumber: 1,
            modelIdentifier: "ComfyUI/SDXL"
        );

        var fpFlux = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot,
            profile: snapshot.GenerationProfile,
            derivedSeed: 100L,
            attemptNumber: 1,
            modelIdentifier: "ComfyUI/Flux.1-Dev"
        );

        Assert.NotEqual(fpSdxl, fpFlux);
    }

    [Fact]
    public void ComputeFingerprint_DifferentMitigationAction_ProducesDifferentFingerprint()
    {
        var jobId = Guid.NewGuid();
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var fpPass = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot,
            profile: snapshot.GenerationProfile,
            derivedSeed: 100L,
            attemptNumber: 1,
            mitigationAction: "Pass"
        );

        var fpIsolated = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot,
            profile: snapshot.GenerationProfile,
            derivedSeed: 100L,
            attemptNumber: 1,
            mitigationAction: "RetryIsolated"
        );

        Assert.NotEqual(fpPass, fpIsolated);
    }

    [Fact]
    public void ComputeFingerprint_DifferentConditioningParametersJson_ProducesDifferentFingerprint()
    {
        var jobId = Guid.NewGuid();
        var snapshot1 = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault() with { ParametersJson = "{\"ipAdapter\":{\"weight\":0.60}}" }
        );

        var snapshot2 = snapshot1 with
        {
            GenerationProfile = snapshot1.GenerationProfile with { ParametersJson = "{\"ipAdapter\":{\"weight\":0.80}}" }
        };

        var fp1 = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot1,
            profile: snapshot1.GenerationProfile,
            derivedSeed: 100L,
            attemptNumber: 1
        );

        var fp2 = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot2,
            profile: snapshot2.GenerationProfile,
            derivedSeed: 100L,
            attemptNumber: 1
        );

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void ComputeFingerprint_DifferentWorkflowOrVersion_ProducesDifferentFingerprint()
    {
        var jobId = Guid.NewGuid();
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var fpV1 = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot,
            profile: snapshot.GenerationProfile,
            derivedSeed: 100L,
            attemptNumber: 1,
            workflow: "VisualIdentity",
            workflowVersion: 1
        );

        var fpV2 = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot,
            profile: snapshot.GenerationProfile,
            derivedSeed: 100L,
            attemptNumber: 1,
            workflow: "VisualIdentity",
            workflowVersion: 2
        );

        var fpOtherWorkflow = _fingerprintService.ComputeFingerprint(
            jobId: jobId,
            snapshot: snapshot,
            profile: snapshot.GenerationProfile,
            derivedSeed: 100L,
            attemptNumber: 1,
            workflow: "VisualContinuity",
            workflowVersion: 1
        );

        Assert.NotEqual(fpV1, fpV2);
        Assert.NotEqual(fpV1, fpOtherWorkflow);
    }
}
