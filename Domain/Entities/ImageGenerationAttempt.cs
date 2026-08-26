using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Durable ledger tracking discrete generation attempts for idempotency, crash-safety, and diagnostic history.
/// Invariant: Unique per GenerationFingerprint in the database.
/// Supports distributed worker leasing to avoid concurrent execution races for the same attempt fingerprint.
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
    public string? ClaimedBy { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? LeaseUntil { get; private set; }
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
        GenerationAttemptStatus status = GenerationAttemptStatus.Running,
        string? claimedBy = null,
        DateTime? startedAt = null,
        DateTime? leaseUntil = null)
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
        ClaimedBy = claimedBy;
        StartedAt = startedAt;
        LeaseUntil = leaseUntil;
    }

    public void SetProviderJobId(string providerJobId)
    {
        ProviderJobId = providerJobId;
        Touch();
    }

    public bool TryClaim(string workerId, DateTime now, TimeSpan leaseDuration)
    {
        if (Status == GenerationAttemptStatus.Succeeded)
            return false;

        if (Status == GenerationAttemptStatus.Running &&
            ClaimedBy != null &&
            ClaimedBy != workerId &&
            LeaseUntil.HasValue &&
            LeaseUntil.Value > now)
        {
            return false;
        }

        ClaimedBy = workerId;
        StartedAt = now;
        LeaseUntil = now.Add(leaseDuration);
        Status = GenerationAttemptStatus.Running;
        Touch();
        return true;
    }

    public void Claim(string workerId, DateTime now, TimeSpan leaseDuration)
    {
        if (!TryClaim(workerId, now, leaseDuration))
        {
            throw new InvalidOperationException($"Cannot claim attempt {Id}: attempt is currently {Status} or under active lease by {ClaimedBy}.");
        }
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
