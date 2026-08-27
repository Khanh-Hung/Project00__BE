namespace Application.Interfaces;

/// <summary>
/// Reconciles orphan or unreferenced generation artifacts.
/// Ensures unverified candidate artifacts generated before a worker crash are never promoted to IsCurrent,
/// preserves historical audit lineages, and demotes illegal unaccepted current flags.
/// </summary>
public interface IArtifactReconciliationService
{
    /// <summary>
    /// Scans and reconciles artifact anomalies:
    /// 1. Demotes any SceneImage where IsCurrent=true but the owning ImageGenerationJob was Failed, Cancelled, or unaccepted.
    /// 2. Returns the count of demoted artifacts.
    /// </summary>
    Task<int> ReconcileOrphanArtifactsAsync(CancellationToken ct = default);
}
