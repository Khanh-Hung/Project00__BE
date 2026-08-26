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
    public float? IdentitySimilarity { get; private set; }
    /// <summary>
    /// Obsolete backwards-compatibility alias for IdentitySimilarity.
    /// </summary>
    [Obsolete("Use IdentitySimilarity instead. Whole-image CLIP similarity proxy measures overall canonical visual identity, not isolated face mesh.", error: false)]
    public float? FaceSimilarity => IdentitySimilarity;
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

    public void StartEvaluating(DateTime now, string? workerId = null)
    {
        if (Status == GenerationAttemptStatus.Succeeded || Status == GenerationAttemptStatus.Failed || Status == GenerationAttemptStatus.Quarantined)
            throw new InvalidOperationException($"Cannot evaluate attempt {Id} in terminal status {Status}.");

        if (workerId != null && ClaimedBy != null && ClaimedBy != workerId)
        {
            throw new InvalidOperationException($"Cannot evaluate attempt {Id}: attempt is claimed by worker '{ClaimedBy}', not '{workerId}'.");
        }

        Status = GenerationAttemptStatus.Evaluating;
        Touch();
    }

    public void MarkSucceeded(string imageUrl, string? providerJobId, float? identitySimilarity, float? featScore, DateTime completedAt, string? workerId = null)
    {
        if (Status == GenerationAttemptStatus.Succeeded)
            throw new InvalidOperationException($"Attempt {Id} is already marked Succeeded.");

        if (workerId != null && ClaimedBy != null && ClaimedBy != workerId)
            throw new InvalidOperationException($"Cannot mark attempt {Id} succeeded: worker '{workerId}' is not the owner ('{ClaimedBy}').");

        ImageUrl = imageUrl;
        ProviderJobId = providerJobId ?? ProviderJobId;
        IdentitySimilarity = identitySimilarity;
        FeatureScore = featScore;
        Status = GenerationAttemptStatus.Succeeded;
        CompletedAt = completedAt;
        Touch();
    }

    public void MarkDegraded(string imageUrl, string? providerJobId, float? identitySimilarity, float? featScore, DateTime completedAt, string? workerId = null)
    {
        if (Status == GenerationAttemptStatus.Succeeded)
            throw new InvalidOperationException($"Attempt {Id} is already in terminal state Succeeded.");

        if (workerId != null && ClaimedBy != null && ClaimedBy != workerId)
            throw new InvalidOperationException($"Cannot mark attempt {Id} degraded: worker '{workerId}' is not the owner ('{ClaimedBy}').");

        ImageUrl = imageUrl;
        ProviderJobId = providerJobId ?? ProviderJobId;
        IdentitySimilarity = identitySimilarity;
        FeatureScore = featScore;
        Status = GenerationAttemptStatus.Degraded;
        CompletedAt = completedAt;
        Touch();
    }

    public void MarkQuarantined(string imageUrl, string? providerJobId, float? identitySimilarity, float? featScore, DateTime completedAt, string? workerId = null)
    {
        if (Status == GenerationAttemptStatus.Succeeded)
            throw new InvalidOperationException($"Attempt {Id} is already in terminal state Succeeded.");

        if (workerId != null && ClaimedBy != null && ClaimedBy != workerId)
            throw new InvalidOperationException($"Cannot mark attempt {Id} quarantined: worker '{workerId}' is not the owner ('{ClaimedBy}').");

        ImageUrl = imageUrl;
        ProviderJobId = providerJobId ?? ProviderJobId;
        IdentitySimilarity = identitySimilarity;
        FeatureScore = featScore;
        Status = GenerationAttemptStatus.Quarantined;
        CompletedAt = completedAt;
        Touch();
    }

    public void MarkFailed(string errorMessage, DateTime completedAt, string? workerId = null)
    {
        if (Status == GenerationAttemptStatus.Succeeded)
            throw new InvalidOperationException($"Attempt {Id} is already in terminal state Succeeded.");

        if (workerId != null && ClaimedBy != null && ClaimedBy != workerId)
            throw new InvalidOperationException($"Cannot mark attempt {Id} failed: worker '{workerId}' is not the owner ('{ClaimedBy}').");

        ErrorMessage = errorMessage;
        Status = GenerationAttemptStatus.Failed;
        CompletedAt = completedAt;
        Touch();
    }
}
