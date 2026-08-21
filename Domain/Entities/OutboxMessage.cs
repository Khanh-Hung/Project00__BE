using Domain.Common;
using Domain.Common.DateTimes;
using Domain.Enums;

namespace Domain.Entities;

public sealed class OutboxMessage : BaseEntity
{
    public string EventType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public OutboxStatus Status { get; private set; } = OutboxStatus.Pending;
    public int RetryCount { get; private set; }
    public int MaxRetries { get; private set; } = 3;
    public string? LastError { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public DateTime? NextRetryAt { get; private set; }
    public DateTime? ProcessingStartedAt { get; private set; }
    public string? ClaimedBy { get; private set; }

    private OutboxMessage() { } // EF Core

    public OutboxMessage(
        string eventType,
        string payloadJson,
        int maxRetries = 3)
    {
        EventType = eventType;
        PayloadJson = payloadJson;
        Status = OutboxStatus.Pending;
        MaxRetries = maxRetries;
        RetryCount = 0;
        NextRetryAt = null;
        ProcessingStartedAt = null;
        ClaimedBy = null;
    }

    public void MarkProcessing(string? workerId = null, DateTime? now = null)
    {
        Status = OutboxStatus.Processing;
        ClaimedBy = workerId;
        ProcessingStartedAt = now ?? Clock.Now;
        Touch();
    }

    public void MarkCompleted(DateTime processedAt)
    {
        Status = OutboxStatus.Completed;
        ProcessedAt = processedAt;
        LastError = null;
        NextRetryAt = null;
        ProcessingStartedAt = null;
        ClaimedBy = null;
        Touch();
    }

    public void MarkDeferred(DateTime nextRetryAt)
    {
        Status = OutboxStatus.Pending;
        NextRetryAt = nextRetryAt;
        ProcessingStartedAt = null;
        ClaimedBy = null;
        // Invariant: Deferring due to predecessor does NOT increment RetryCount
        Touch();
    }

    public void MarkFailed(string error, DateTime failedAt, bool isTransient = true)
    {
        LastError = error;
        ProcessedAt = failedAt;
        ProcessingStartedAt = null;
        ClaimedBy = null;

        if (!isTransient)
        {
            // Non-transient errors (400, 422, 404 reference) fast-fail immediately
            Status = OutboxStatus.Failed;
            NextRetryAt = null;
            RetryCount = MaxRetries;
        }
        else
        {
            RetryCount++;
            if (RetryCount >= MaxRetries)
            {
                Status = OutboxStatus.Failed;
                NextRetryAt = null;
            }
            else
            {
                Status = OutboxStatus.Pending;
                // Exponential backoff with jitter: 2^RetryCount * 5s (+/- 2s), max 300s
                var baseDelaySeconds = Math.Min(300, Math.Pow(2, RetryCount) * 5);
                var jitter = Random.Shared.Next(-2, 3);
                var totalDelay = Math.Max(2, (int)baseDelaySeconds + jitter);
                NextRetryAt = failedAt.AddSeconds(totalDelay);
            }
        }

        Touch();
    }

    public void ReclaimStaleProcessing(DateTime now)
    {
        Status = OutboxStatus.Pending;
        ProcessingStartedAt = null;
        ClaimedBy = null;
        NextRetryAt = now;
        Touch();
    }
}
