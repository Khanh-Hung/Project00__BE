using Domain.Common;
using Domain.Common.DateTimes;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Domain entity representing a discrete, trackable image generation job.
/// Supports state transitions: Pending -> Processing -> Completed | Failed.
/// Captures execution metadata and error classification for resilience and idempotency.
/// </summary>
public sealed class ImageGenerationJob : BaseEntity
{
    public Guid SessionId { get; private set; }
    public Guid TurnId { get; private set; }
    public Guid CharacterId { get; private set; }
    public int SceneRevision { get; private set; }
    public string Provider { get; private set; } = "ComfyUI";
    public string? ProviderJobId { get; private set; }
    public ImageJobStatus Status { get; private set; } = ImageJobStatus.Pending;
    public int AttemptCount { get; private set; } = 0;
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public bool IsRetryable { get; private set; }
    public string Workflow { get; private set; } = "VisualIdentity";
    public int WorkflowVersion { get; private set; } = 1;
    public string? GenerationMetadataJson { get; private set; }

    private ImageGenerationJob() { } // EF Core

    public ImageGenerationJob(
        Guid sessionId,
        Guid turnId,
        Guid characterId,
        int sceneRevision,
        string provider = "ComfyUI",
        string workflow = "VisualIdentity",
        int workflowVersion = 1,
        string? generationMetadataJson = null)
    {
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
    }

    public void MarkProcessing(string? providerJobId = null, DateTime? startedAt = null)
    {
        Status = ImageJobStatus.Processing;
        ProviderJobId = providerJobId ?? ProviderJobId;
        StartedAt = startedAt ?? Clock.Now;
        FailureReason = null;
        IsRetryable = false;
        CompletedAt = null;
        AttemptCount++;
        Touch();
    }

    public void MarkCompleted(DateTime? completedAt = null, string? metadataJson = null)
    {
        Status = ImageJobStatus.Completed;
        CompletedAt = completedAt ?? Clock.Now;
        FailureReason = null;
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            GenerationMetadataJson = metadataJson;
        }
        Touch();
    }

    public void MarkFailed(string reason, bool isRetryable, DateTime? failedAt = null)
    {
        Status = ImageJobStatus.Failed;
        FailureReason = reason;
        IsRetryable = isRetryable;
        CompletedAt = failedAt ?? Clock.Now;
        Touch();
    }

    public void MarkCancelled(DateTime? cancelledAt = null)
    {
        Status = ImageJobStatus.Cancelled;
        CompletedAt = cancelledAt ?? Clock.Now;
        Touch();
    }

    public void ResetToPending()
    {
        Status = ImageJobStatus.Pending;
        StartedAt = null;
        Touch();
    }
}
