using System.Text.Json;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable audit and provenance record tracking the exact generation parameters,
/// conditioning configuration, seed derivation, and quality evaluation results that produced a SceneImage artifact.
/// </summary>
public sealed record GenerationProvenance
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Guid GenerationRequestId { get; set; }
    public Guid JobId { get; set; }
    public Guid AttemptId { get; set; }
    public int SceneRevision { get; set; }
    public long DerivedSeed { get; set; }
    public string GenerationFingerprint { get; set; } = string.Empty;
    public string Workflow { get; set; } = "VisualIdentity";
    public int WorkflowVersion { get; set; } = 1;
    public string ModelIdentifier { get; set; } = "ComfyUI/SDXL";
    public float Slot1Weight { get; set; } = 1.0f;
    public float Slot2Weight { get; set; } = 0.0f;
    public string Slot2ConditioningMode { get; set; } = "Disabled";
    public string MitigationAction { get; set; } = "None";
    public float? IdentitySimilarity { get; set; }
    public float? FeatureScore { get; set; }
    public string IdentityStatus { get; set; } = "Passed";
    public DateTime CreatedAt { get; set; }

    public GenerationProvenance()
    {
    }

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
        DateTime? createdAt = null)
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
        CreatedAt = createdAt ?? DateTime.UtcNow;
    }

    public string ToJson() => JsonSerializer.Serialize(this, s_jsonOptions);

    public static GenerationProvenance? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<GenerationProvenance>(json, s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
