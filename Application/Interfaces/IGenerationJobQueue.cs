using Application.DTOs;

namespace Application.Interfaces;

/// <summary>
/// Queue boundary decoupling generation requests from discrete GPU worker execution.
/// Supports bounded capacity, atomic backpressure admission, true priority sorting, and duplicate suppression.
/// </summary>
public interface IGenerationJobQueue
{
    /// <summary>
    /// Enqueues an authoritative generation work item.
    /// Throws InvalidOperationException if queue capacity is exceeded (atomic backpressure rejection).
    /// </summary>
    ValueTask EnqueueAsync(GenerationWorkItem item, CancellationToken ct = default);

    /// <summary>
    /// Dequeues the next highest-priority generation work item, or null if the queue is empty.
    /// </summary>
    ValueTask<GenerationWorkItem?> DequeueAsync(CancellationToken ct = default);

    /// <summary>
    /// Current number of items waiting in the queue.
    /// </summary>
    int CurrentDepth { get; }

    /// <summary>
    /// Maximum capacity of the queue before backpressure rejection is triggered.
    /// </summary>
    int Capacity { get; }
}
