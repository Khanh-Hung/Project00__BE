using Application.DTOs;

namespace Application.Interfaces;

/// <summary>
/// Service coordinating authoritative artifact promotion, visual session state advancement,
/// and artifact superseding within the application layer.
/// </summary>
public interface IVisualArtifactService
{
    Task<ArtifactAcceptanceResult> PromoteAsync(
        Guid generationJobId,
        Guid attemptId,
        CancellationToken ct = default);
}
