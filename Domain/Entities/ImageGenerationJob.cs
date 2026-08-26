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
    public Guid UserId { get; private set; }
    public int SceneRevision { get; private set; }
    public Guid GenerationRequestId { get; private set; }
    public Guid? OutboxMessageId { get; private set; }
    public string Provider { get; private set; } = "ComfyUI";
    public string? ProviderJobId { get; private set; }
    public ImageJobStatus Status { get; private set; } = ImageJobStatus.Pending;
    public int AttemptCount { get; private set; } = 0;
    public Guid? AcceptedAttemptId { get; private set; }
    public Guid? QuarantinedAttemptId { get; private set; }
    public int CurrentAttemptNumber { get; private set; } = 0;
    public string? ClaimedBy { get; private set; }
    public DateTime? LeaseUntil { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public bool IsRetryable { get; private set; }
    public int RetryCount { get; private set; } = 0;
    public DateTime? NextAttemptAt { get; private set; }
    public bool CancellationRequested { get; private set; } = false;
    public DateTime? LastHeartbeatAt { get; private set; }
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
        Guid? userId = null,
        Guid? outboxMessageId = null,
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
        UserId = userId ?? Guid.Empty;
        OutboxMessageId = outboxMessageId;
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
        if (Status != ImageJobStatus.Pending)
            throw new InvalidOperationException($"Cannot queue job {Id}: transition to Queued is only allowed from Pending, but current status is {Status}.");

        Status = ImageJobStatus.Queued;
        Touch();
    }

    public void ScheduleRetry(DateTime nextAttemptAt, string reason, DateTime now)
    {
        Status = ImageJobStatus.Queued;
        RetryCount++;
        NextAttemptAt = nextAttemptAt;
        FailureReason = reason;
        IsRetryable = true;
        ClaimedBy = null;
        LeaseUntil = null;
        Version++;
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

    public void StartRunning(string workerId, TimeSpan leaseDuration, DateTime now)
    {
        if (!TryClaim(workerId, leaseDuration, now))
        {
            throw new InvalidOperationException($"Cannot transition job {Id} to Running: job is in status {Status} or under active lease by {ClaimedBy}.");
        }
    }

    public void MarkEvaluating(DateTime now, string workerId)
    {
        if (Status != ImageJobStatus.Processing)
            throw new InvalidOperationException($"Cannot evaluate job {Id}: transition to Evaluating is only allowed from Processing, but current status is {Status}.");

        EnsureActiveLease(workerId, now);

        Status = ImageJobStatus.Evaluating;
        Version++;
        Touch();
    }

    public void AcceptAttempt(Guid attemptId, DateTime now, string workerId, string? metadataJson = null)
    {
        if (attemptId == Guid.Empty)
            throw new ArgumentException("AcceptedAttemptId cannot be empty.", nameof(attemptId));

        if (Status != ImageJobStatus.Processing && Status != ImageJobStatus.Evaluating)
            throw new InvalidOperationException($"Cannot accept attempt for job {Id}: acceptance is only allowed from Processing or Evaluating, but current status is {Status}.");

        EnsureActiveLease(workerId, now);

        AcceptedAttemptId = attemptId;
        QuarantinedAttemptId = null;
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

    public void Quarantine(Guid? lastAttemptId, string reason, DateTime now, string workerId)
    {
        if (Status != ImageJobStatus.Processing && Status != ImageJobStatus.Evaluating)
            throw new InvalidOperationException($"Cannot quarantine job {Id}: quarantine is only allowed from Processing or Evaluating, but current status is {Status}.");

        EnsureActiveLease(workerId, now);

        AcceptedAttemptId = null;
        QuarantinedAttemptId = lastAttemptId;
        Status = ImageJobStatus.Quarantined;
        FailureReason = reason;
        CompletedAt = now;
        LeaseUntil = null;
        Version++;
        Touch();
    }

    public void Fail(string reason, bool isRetryable, DateTime now, string workerId)
    {
        if (Status != ImageJobStatus.Processing && Status != ImageJobStatus.Evaluating)
            throw new InvalidOperationException($"Cannot fail job {Id}: fail transition is only allowed from Processing or Evaluating, but current status is {Status}.");

        EnsureActiveLease(workerId, now);

        Status = ImageJobStatus.Failed;
        FailureReason = reason;
        IsRetryable = isRetryable;
        CompletedAt = now;
        LeaseUntil = null;
        Version++;
        Touch();
    }

    private void EnsureActiveLease(string workerId, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId, nameof(workerId));

        if (ClaimedBy == null || !string.Equals(ClaimedBy, workerId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cannot mutate job {Id}: worker '{workerId}' is not the active owner ('{ClaimedBy ?? "null"}').");

        if (!LeaseUntil.HasValue || LeaseUntil.Value <= now)
            throw new InvalidOperationException($"Cannot mutate job {Id}: worker lease expired at {LeaseUntil?.ToString("O") ?? "null"} (now: {now:O}).");
    }

    public void RequestCancellation(DateTime now)
    {
        if (IsTerminal())
            return; // Idempotent no-op on already terminal jobs

        CancellationRequested = true;

        if (Status == ImageJobStatus.Pending || Status == ImageJobStatus.Queued)
        {
            Status = ImageJobStatus.Cancelled;
            CompletedAt = now;
            LeaseUntil = null;
        }

        Version++;
        Touch();
    }

    public void ScheduleRetry(DateTime nextAttemptAt, string reason, DateTime now, string workerId)
    {
        if (Status != ImageJobStatus.Processing && Status != ImageJobStatus.Evaluating)
            throw new InvalidOperationException($"Cannot schedule retry for job {Id}: retry transition is only allowed from Processing or Evaluating, but current status is {Status}.");

        EnsureActiveLease(workerId, now);

        Status = ImageJobStatus.Queued;
        RetryCount++;
        NextAttemptAt = nextAttemptAt;
        FailureReason = reason;
        IsRetryable = true;
        ClaimedBy = null;
        LeaseUntil = null;
        Version++;
        Touch();
    }

    public void RecordHeartbeat(DateTime now, string workerId)
    {
        EnsureActiveLease(workerId, now);
        LastHeartbeatAt = now;
        Touch();
    }

    public void Cancel(DateTime now)
    {
        if (IsTerminal())
            throw new InvalidOperationException($"Cannot cancel job {Id} because it is already in terminal state {Status}.");

        Status = ImageJobStatus.Cancelled;
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
