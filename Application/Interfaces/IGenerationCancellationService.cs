namespace Application.Interfaces;

/// <summary>
/// First-class generation cancellation coordinator.
/// Handles idempotent cancellation requests across Queued, Processing, and Evaluating phases,
/// propagating target-specific interrupt signals to GPU providers and preventing late artifact promotion.
/// </summary>
public interface IGenerationCancellationService
{
    /// <summary>
    /// Requests cancellation of a generation job.
    /// Returns true if cancellation was successfully recorded or applied; false if job is already terminal.
    /// </summary>
    Task<bool> RequestCancellationAsync(Guid jobId, string reason = "User cancelled generation", CancellationToken ct = default);
}
