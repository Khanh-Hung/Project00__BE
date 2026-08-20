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
    }

    public void MarkProcessing()
    {
        Status = OutboxStatus.Processing;
        Touch();
    }

    public void MarkCompleted(DateTime processedAt)
    {
        Status = OutboxStatus.Completed;
        ProcessedAt = processedAt;
        LastError = null;
        Touch();
    }

    public void MarkFailed(string error, DateTime failedAt)
    {
        RetryCount++;
        LastError = error;
        Status = RetryCount >= MaxRetries ? OutboxStatus.Failed : OutboxStatus.Pending;
        ProcessedAt = failedAt;
        Touch();
    }
}
