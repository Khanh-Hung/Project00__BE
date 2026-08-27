using Application.DTOs;

namespace Application.Interfaces;

/// <summary>
/// Service managing visual artifact retention policies, evaluation, and safe asynchronous cleanup.
/// </summary>
public interface IArtifactRetentionService
{
    /// <summary>
    /// Evaluates whether a given artifact is protected from deletion or eligible for cleanup.
    /// Invariants:
    /// - Current artifacts are protected indefinitely.
    /// - Active predecessors referenced by current artifacts are protected.
    /// - Artifacts linked to in-flight generation jobs are protected.
    /// - Quarantined and orphaned artifacts exceeding TTL are eligible.
    /// </summary>
    Task<ArtifactRetentionEvaluationResult> EvaluateEligibilityAsync(
        Guid artifactId,
        CancellationToken ct = default);

    /// <summary>
    /// Scans and safely marks/cleans expired non-protected artifacts.
    /// </summary>
    Task<int> CleanupExpiredArtifactsAsync(
        TimeSpan quarantinedTtl,
        TimeSpan orphanTtl,
        CancellationToken ct = default);
}
