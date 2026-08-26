using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Durable ledger tracking discrete generation attempts for idempotency, crash-safety, and diagnostic history.
/// Invariant: Unique per GenerationFingerprint in the database.
/// </summary>
public sealed class ImageGenerationAttempt : BaseEntity
{
    public Guid GenerationJobId { get; private set; }
    public Guid TurnId { get; private set; }
    public int SceneRevision { get; private set; }
    public int AttemptNumber { get; private set; }
    public long DerivedSeed { get; private set; }
    public string ParametersJson { get; private set; } = string.Empty;
    public string GenerationFingerprint { get; private set; } = string.Empty;
    public GenerationAttemptStatus Status { get; private set; } = GenerationAttemptStatus.Running;
    public string? ImageUrl { get; private set; }
    public string? ProviderJobId { get; private set; }
    public float? FaceSimilarity { get; private set; }
    public float? FeatureScore { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? ErrorMessage { get; private set; }

    private ImageGenerationAttempt() { } // EF Core

    public ImageGenerationAttempt(
        Guid generationJobId,
        Guid turnId,
        int sceneRevision,
        int attemptNumber,
        long derivedSeed,
        string parametersJson,
        string generationFingerprint,
        GenerationAttemptStatus status = GenerationAttemptStatus.Running)
    {
        Id = Guid.NewGuid();
        GenerationJobId = generationJobId;
        TurnId = turnId;
        SceneRevision = sceneRevision;
        AttemptNumber = attemptNumber;
        DerivedSeed = derivedSeed;
        ParametersJson = parametersJson;
        GenerationFingerprint = generationFingerprint;
        Status = status;
    }

    public void SetProviderJobId(string providerJobId)
    {
        ProviderJobId = providerJobId;
        Touch();
    }

    public void MarkSucceeded(string imageUrl, string? providerJobId, float? faceSim, float? featScore, DateTime completedAt)
    {
        ImageUrl = imageUrl;
        ProviderJobId = providerJobId ?? ProviderJobId;
        FaceSimilarity = faceSim;
        FeatureScore = featScore;
        Status = GenerationAttemptStatus.Succeeded;
        CompletedAt = completedAt;
        Touch();
    }

    public void MarkDegraded(string imageUrl, string? providerJobId, float? faceSim, float? featScore, DateTime completedAt)
    {
        ImageUrl = imageUrl;
        ProviderJobId = providerJobId ?? ProviderJobId;
        FaceSimilarity = faceSim;
        FeatureScore = featScore;
        Status = GenerationAttemptStatus.Degraded;
        CompletedAt = completedAt;
        Touch();
    }

    public void MarkFailed(string errorMessage, DateTime completedAt)
    {
        ErrorMessage = errorMessage;
        Status = GenerationAttemptStatus.Failed;
        CompletedAt = completedAt;
        Touch();
    }
}
