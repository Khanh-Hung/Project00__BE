using System.Text.Json;
using Domain.Entities;
using Domain.ValueObjects;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class ArtifactProvenanceTests
{
    [Fact]
    public void GenerationProvenance_SerializationAndDeserialization_RoundTripsAccurately()
    {
        var requestId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var provenance = new GenerationProvenance(
            generationRequestId: requestId,
            jobId: jobId,
            attemptId: attemptId,
            sceneRevision: 3,
            derivedSeed: 987654321L,
            generationFingerprint: "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            workflow: "VisualIdentity",
            workflowVersion: 1,
            modelIdentifier: "ComfyUI/SDXL",
            slot1Weight: 0.65f,
            slot2Weight: 0.06f,
            slot2ConditioningMode: "SceneStyleContinuity",
            mitigationAction: "RetryAttenuated",
            identitySimilarity: 0.884f,
            featureScore: 0.725f,
            identityStatus: "Passed",
            createdAt: now
        );

        var json = provenance.ToJson();
        Assert.False(string.IsNullOrWhiteSpace(json));

        var deserialized = GenerationProvenance.FromJson(json);
        Assert.NotNull(deserialized);
        Assert.Equal(requestId, deserialized.GenerationRequestId);
        Assert.Equal(jobId, deserialized.JobId);
        Assert.Equal(attemptId, deserialized.AttemptId);
        Assert.Equal(3, deserialized.SceneRevision);
        Assert.Equal(987654321L, deserialized.DerivedSeed);
        Assert.Equal("abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890", deserialized.GenerationFingerprint);
        Assert.Equal("VisualIdentity", deserialized.Workflow);
        Assert.Equal(1, deserialized.WorkflowVersion);
        Assert.Equal(0.65f, deserialized.Slot1Weight);
        Assert.Equal(0.06f, deserialized.Slot2Weight);
        Assert.Equal("SceneStyleContinuity", deserialized.Slot2ConditioningMode);
        Assert.Equal("RetryAttenuated", deserialized.MitigationAction);
        Assert.Equal(0.884f, deserialized.IdentitySimilarity);
        Assert.Equal(0.725f, deserialized.FeatureScore);
        Assert.Equal("Passed", deserialized.IdentityStatus);
    }

    [Fact]
    public void SceneImage_AttachProvenance_PersistsAndRestoresProvenanceObject()
    {
        var sessionId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        var artifact = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://storage.example.com/scene1.png",
            prompt: "a knight standing guard",
            generationRequestId: requestId,
            generationJobId: jobId
        );

        Assert.Null(artifact.ProvenanceJson);
        Assert.Null(artifact.GetProvenance());

        var provenance = new GenerationProvenance(
            generationRequestId: requestId,
            jobId: jobId,
            attemptId: attemptId,
            sceneRevision: 1,
            derivedSeed: 42L,
            generationFingerprint: "fp123456",
            slot1Weight: 0.60f,
            slot2Weight: 0.12f,
            slot2ConditioningMode: "SceneStyleContinuity",
            mitigationAction: "Pass",
            identitySimilarity: 0.91f,
            featureScore: 0.85f,
            identityStatus: "Passed"
        );

        artifact.AttachProvenance(provenance);

        Assert.NotNull(artifact.ProvenanceJson);
        var retrieved = artifact.GetProvenance();
        Assert.NotNull(retrieved);
        Assert.Equal(requestId, retrieved.GenerationRequestId);
        Assert.Equal(jobId, retrieved.JobId);
        Assert.Equal(attemptId, retrieved.AttemptId);
        Assert.Equal("fp123456", retrieved.GenerationFingerprint);
        Assert.Equal(0.60f, retrieved.Slot1Weight);
        Assert.Equal(0.12f, retrieved.Slot2Weight);
        Assert.Equal("SceneStyleContinuity", retrieved.Slot2ConditioningMode);
        Assert.Equal("Pass", retrieved.MitigationAction);
        Assert.Equal(0.91f, retrieved.IdentitySimilarity);
    }

    [Fact]
    public void FromJson_WhenNullOrEmpty_ReturnsNull()
    {
        Assert.Null(GenerationProvenance.FromJson(null));
        Assert.Null(GenerationProvenance.FromJson(""));
        Assert.Null(GenerationProvenance.FromJson("   "));
    }

    [Fact]
    public void FromJson_WhenCorruptJson_ThrowsJsonException()
    {
        var corruptedJson = "{ \"generationRequestId\": \"not-a-valid-guid\", bad json here";

        Assert.ThrowsAny<JsonException>(() => GenerationProvenance.FromJson(corruptedJson));
    }
}
