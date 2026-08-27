using System.Text.Json;
using System.Text.Json.Serialization;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable audit and provenance record tracking the exact generation parameters,
/// conditioning configuration, seed derivation, and quality evaluation results that produced a SceneImage artifact.
/// Note: CreatedAt represents audit metadata capturing when the artifact was produced by the orchestrator,
/// and is not an input to deterministic seed derivation or uniqueness fingerprinting.
/// </summary>
public sealed record GenerationProvenance
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Guid GenerationRequestId { get; init; }
    public Guid JobId { get; init; }
    public Guid AttemptId { get; init; }
    public int SceneRevision { get; init; }
    public long DerivedSeed { get; init; }
    public string GenerationFingerprint { get; init; } = string.Empty;
    public string Workflow { get; init; } = "VisualIdentity";
    public int WorkflowVersion { get; init; } = 1;
    public string ModelIdentifier { get; init; } = "ComfyUI/SDXL";
    public float Slot1Weight { get; init; } = 1.0f;
    public float Slot2Weight { get; init; } = 0.0f;
    public string Slot2ConditioningMode { get; init; } = "Disabled";
    public string MitigationAction { get; init; } = "None";
    public float? IdentitySimilarity { get; init; }
    public float? FeatureScore { get; init; }
    public string IdentityStatus { get; init; } = "Passed";
    public DateTime CreatedAt { get; init; }

    public GenerationProvenance()
    {
    }

    [JsonConstructor]
    public GenerationProvenance(
        Guid generationRequestId,
        Guid jobId,
        Guid attemptId,
        int sceneRevision,
        long derivedSeed,
        string generationFingerprint,
        string workflow = "VisualIdentity",
        int workflowVersion = 1,
        string modelIdentifier = "ComfyUI/SDXL",
        float slot1Weight = 1.0f,
        float slot2Weight = 0.0f,
        string slot2ConditioningMode = "Disabled",
        string mitigationAction = "None",
        float? identitySimilarity = null,
        float? featureScore = null,
        string identityStatus = "Passed",
        DateTime createdAt = default)
    {
        GenerationRequestId = generationRequestId;
        JobId = jobId;
        AttemptId = attemptId;
        SceneRevision = sceneRevision;
        DerivedSeed = derivedSeed;
        GenerationFingerprint = generationFingerprint ?? string.Empty;
        Workflow = workflow ?? "VisualIdentity";
        WorkflowVersion = workflowVersion;
        ModelIdentifier = modelIdentifier ?? "ComfyUI/SDXL";
        Slot1Weight = slot1Weight;
        Slot2Weight = slot2Weight;
        Slot2ConditioningMode = slot2ConditioningMode ?? "Disabled";
        MitigationAction = mitigationAction ?? "None";
        IdentitySimilarity = identitySimilarity;
        FeatureScore = featureScore;
        IdentityStatus = identityStatus ?? "Passed";
        CreatedAt = createdAt != default ? createdAt : DateTime.UtcNow;
    }

    public string ToJson() => JsonSerializer.Serialize(this, s_jsonOptions);

    /// <summary>
    /// Deserializes a provenance record from JSON.
    /// Returns null if json is null or whitespace.
    /// Throws JsonException if json is invalid or corrupted.
    /// </summary>
    public static GenerationProvenance? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<GenerationProvenance>(json, s_jsonOptions);
    }
}
