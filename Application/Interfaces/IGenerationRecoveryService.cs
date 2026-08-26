namespace Application.Interfaces;

/// <summary>
/// Authoritative lease crash-recovery and durable re-dispatch service.
/// Detects abandoned worker leases, fences stale workers via CAS increments,
/// and re-dispatches due pending/queued jobs into the queue.
/// </summary>
public interface IGenerationRecoveryService
{
    /// <summary>
    /// Scans for and recovers all expired worker leases, and re-dispatches due pending/queued jobs.
    /// Returns the number of successfully recovered / transitioned jobs.
    /// </summary>
    Task<int> RecoverExpiredJobsAsync(DateTime? referenceTime = null, CancellationToken ct = default);
}
