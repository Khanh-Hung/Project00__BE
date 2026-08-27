using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

public sealed record ArtifactAcceptanceRequest(
    Guid JobId,
    Guid WinningAttemptId,
    VisualSnapshot Snapshot,
    string ImageUrl,
    string CompiledPrompt,
    string? ResolvedPreviousSceneImageUrl,
    string GenerationFingerprint,
    string? MetadataJson,
    bool IsIdentityPassed,
    string WorkerId,
    Guid OutboxId,
    GenerationProvenance? Provenance = null
);

/// <summary>
/// Service responsible for the atomic compare-and-swap acceptance fencing,
/// artifact lineage promotion/demotion, provenance attachment, and outbox event persistence in a single transactional boundary (P0-1).
/// </summary>
public interface IArtifactAcceptanceService
{
    Task<JobExecutionResult> AcceptAttemptAtomicallyAsync(
        ArtifactAcceptanceRequest request,
        CancellationToken ct = default);
}
