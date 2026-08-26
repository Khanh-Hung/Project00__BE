using Domain.Common;
using Domain.Common.DateTimes;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Domain entity representing a discrete, trackable image generation job.
/// Supports lease-based concurrency: Pending -> Processing (with LeaseUntil) -> Completed | Failed | Cancelled.
/// Captures execution metadata and error classification for resilience and idempotency.
/// </summary>
public sealed class ImageGenerationJob : BaseEntity
{
    public Guid SessionId { get; private set; }
    public Guid TurnId { get; private set; }
    public Guid CharacterId { get; private set; }
    public int SceneRevision { get; private set; }
    public Guid GenerationRequestId { get; private set; }
    public string Provider { get; private set; } = "ComfyUI";
    public string? ProviderJobId { get; private set; }
    public ImageJobStatus Status { get; private set; } = ImageJobStatus.Pending;
    public int AttemptCount { get; private set; } = 0;
    public Guid? AcceptedAttemptId { get; private set; }
    public int CurrentAttemptNumber { get; private set; } = 0;
    public string? ClaimedBy { get; private set; }
    public DateTime? LeaseUntil { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public bool IsRetryable { get; private set; }
    public string Workflow { get; private set; } = "VisualIdentity";
    public int WorkflowVersion { get; private set; } = 1;
    public string? GenerationMetadataJson { get; private set; }

    public uint Version { get; private set; } = 1;

    private ImageGenerationJob() { } // EF Core

    public ImageGenerationJob(
        Guid sessionId,
        Guid turnId,
        Guid characterId,
        int sceneRevision,
        Guid? generationRequestId = null,
        string provider = "ComfyUI",
        string workflow = "VisualIdentity",
        int workflowVersion = 1,
        string? generationMetadataJson = null)
    {
        Id = Guid.NewGuid();
        GenerationRequestId = generationRequestId ?? Guid.NewGuid();
        SessionId = sessionId;
        TurnId = turnId;
        CharacterId = characterId;
        SceneRevision = sceneRevision;
        Provider = provider;
        Workflow = workflow;
        WorkflowVersion = workflowVersion;
        GenerationMetadataJson = generationMetadataJson;
        Status = ImageJobStatus.Pending;
        AttemptCount = 0;
        CurrentAttemptNumber = 0;
        Version = 1;
    }

    public void SetProviderJobId(string providerJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJobId, nameof(providerJobId));
        ProviderJobId = providerJobId;
        Version++;
        Touch();
    }

    public void MarkQueued(DateTime now)
    {
        if (IsTerminal())
            throw new InvalidOperationException($"Cannot queue job {Id} because it is in terminal state {Status}.");

        Status = ImageJobStatus.Queued;
        Touch();
    }

    public bool TryClaim(string workerId, TimeSpan leaseDuration, DateTime now)
    {
        if (Status == ImageJobStatus.Completed || Status == ImageJobStatus.Quarantined || (Status == ImageJobStatus.Failed && !IsRetryable))
        {
            return false;
        }

        // Allow claim if Pending/Queued, or if Processing/Evaluating but lease has expired, or retryable failure
        if (Status == ImageJobStatus.Pending || 
            Status == ImageJobStatus.Queued ||
            ((Status == ImageJobStatus.Processing || Status == ImageJobStatus.Evaluating) && LeaseUntil.HasValue && LeaseUntil.Value <= now) || 
            (Status == ImageJobStatus.Failed && IsRetryable))
        {
            Status = ImageJobStatus.Processing;
            ClaimedBy = workerId;
            LeaseUntil = now.Add(leaseDuration);
            StartedAt = now;
            FailureReason = null;
            IsRetryable = false;
            CompletedAt = null;
            AttemptCount++;
            CurrentAttemptNumber = AttemptCount;
            Version++;
            Touch();
            return true;
        }

        return false;
    }

    public void MarkEvaluating(DateTime now)
    {
        if (IsTerminal())
            throw new InvalidOperationException($"Cannot evaluate job {Id} because it is in terminal state {Status}.");

        Status = ImageJobStatus.Evaluating;
        Touch();
    }

    public void AcceptAttempt(Guid attemptId, DateTime now, string? metadataJson = null)
    {
        if (attemptId == Guid.Empty)
            throw new ArgumentException("AcceptedAttemptId cannot be empty.", nameof(attemptId));

        if (IsTerminal())
            throw new InvalidOperationException($"Cannot accept attempt for job {Id} because it is already in terminal state {Status}.");

        AcceptedAttemptId = attemptId;
        Status = ImageJobStatus.Completed;
        CompletedAt = now;
        LeaseUntil = null;
        FailureReason = null;
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            GenerationMetadataJson = metadataJson;
        }
        Version++;
        Touch();
    }

    public void Quarantine(Guid? lastAttemptId, string reason, DateTime now)
    {
        if (IsTerminal())
            throw new InvalidOperationException($"Cannot quarantine job {Id} because it is in terminal state {Status}.");

        AcceptedAttemptId = lastAttemptId;
        Status = ImageJobStatus.Quarantined;
        FailureReason = reason;
        CompletedAt = now;
        LeaseUntil = null;
        Version++;
        Touch();
    }

    public void ExpireLease(DateTime? expiredAt = null)
    {
        LeaseUntil = expiredAt ?? DateTime.UtcNow.AddMinutes(-1);
        Touch();
    }

    public void MarkProcessing(string? providerJobId = null, string? workerId = null, TimeSpan? leaseDuration = null, DateTime? startedAt = null)
    {
        var now = startedAt ?? Clock.Now;
        Status = ImageJobStatus.Processing;
        ProviderJobId = providerJobId ?? ProviderJobId;
        ClaimedBy = workerId ?? ClaimedBy;
        LeaseUntil = leaseDuration.HasValue ? now.Add(leaseDuration.Value) : (LeaseUntil ?? now.AddMinutes(2));
        StartedAt = now;
        FailureReason = null;
        IsRetryable = false;
        CompletedAt = null;
        AttemptCount++;
        CurrentAttemptNumber = AttemptCount;
        Version++;
        Touch();
    }

    public void MarkCompleted(DateTime? completedAt = null, string? metadataJson = null)
    {
        Status = ImageJobStatus.Completed;
        CompletedAt = completedAt ?? Clock.Now;
        LeaseUntil = null;
        FailureReason = null;
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            GenerationMetadataJson = metadataJson;
        }
        Version++;
        Touch();
    }

    public void MarkFailed(string reason, bool isRetryable, DateTime? failedAt = null)
    {
        Status = ImageJobStatus.Failed;
        FailureReason = reason;
        IsRetryable = isRetryable;
        CompletedAt = failedAt ?? Clock.Now;
        LeaseUntil = null;
        Version++;
        Touch();
    }

    public void MarkCancelled(DateTime? cancelledAt = null)
    {
        Status = ImageJobStatus.Cancelled;
        CompletedAt = cancelledAt ?? Clock.Now;
        LeaseUntil = null;
        Version++;
        Touch();
    }

    public void ResetToPending()
    {
        Status = ImageJobStatus.Pending;
        ClaimedBy = null;
        LeaseUntil = null;
        StartedAt = null;
        Version++;
        Touch();
    }

    private bool IsTerminal() =>
        Status is ImageJobStatus.Completed or ImageJobStatus.Quarantined or ImageJobStatus.Cancelled or (ImageJobStatus.Failed and not ImageJobStatus.Pending);
}
