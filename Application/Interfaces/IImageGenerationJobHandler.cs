using Application.DTOs;

namespace Application.Interfaces;

public enum JobExecutionStatus
{
    Completed,
    Deferred,
    Skipped,
    Failed
}

public sealed record JobExecutionResult(JobExecutionStatus Status, string? Reason = null);

public interface IImageGenerationJobHandler
{
    Task<JobExecutionResult> HandleSceneImageGenerationAsync(
        SceneImageGenerationOutboxPayload payload,
        Guid outboxId,
        string workerId,
        DateTime now,
        CancellationToken ct = default);
}
