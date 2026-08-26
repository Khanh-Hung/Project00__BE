namespace Application.Interfaces;

/// <summary>
/// Queue boundary decoupling generation requests from discrete GPU worker execution.
/// Supports bounded capacity, backpressure limits, prioritization, and duplicate suppression.
/// </summary>
public interface IGenerationJobQueue
{
    /// <summary>
    /// Enqueues a generation job ID with optional priority (higher value = higher priority).
    /// </summary>
    ValueTask EnqueueAsync(Guid jobId, int priority = 0, CancellationToken ct = default);

    /// <summary>
    /// Dequeues the next available generation job ID, or null if the queue is empty.
    /// </summary>
    ValueTask<Guid?> DequeueAsync(CancellationToken ct = default);

    /// <summary>
    /// Current number of items waiting in the queue.
    /// </summary>
    int CurrentDepth { get; }

    /// <summary>
    /// Maximum capacity of the queue before backpressure rejection is triggered.
    /// </summary>
    int Capacity { get; }
}
