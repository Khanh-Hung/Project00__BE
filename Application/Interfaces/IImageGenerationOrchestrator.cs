using Application.DTOs;
using Application.Enums;

namespace Application.Interfaces;

/// <summary>
/// Authoritative orchestrator for the visual generation pipeline.
/// Manages the discrete multi-stage lifecycle: Predecessor Gating -> Job Claiming ->
/// Deterministic Attempt Generation -> Quality Evaluation -> Atomic Attempt Acceptance -> Lineage Artifact Persistence.
/// </summary>
public interface IImageGenerationOrchestrator
{
    Task<JobExecutionResult> OrchestrateSceneImageGenerationAsync(
        SceneImageGenerationOutboxPayload payload,
        Guid outboxId,
        string workerId,
        DateTime now,
        CancellationToken ct = default);
}
