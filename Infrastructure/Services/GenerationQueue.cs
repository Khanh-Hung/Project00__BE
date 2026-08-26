using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Thread-safe in-memory prioritized bounded queue implementation for generation work items.
/// Enforces atomic backpressure admission, true priority ordering with FIFO tie-breaking,
/// and duplicate request suppression.
/// </summary>
public sealed class GenerationQueue : IGenerationJobQueue, IDisposable
{
    private readonly struct PriorityKey : IComparable<PriorityKey>
    {
        public readonly int InvertedPriority;
        public readonly long SequenceNumber;

        public PriorityKey(int priority, long sequenceNumber)
        {
            InvertedPriority = int.MaxValue - priority; // Higher priority -> smaller value (for min-heap)
            SequenceNumber = sequenceNumber;            // Smaller sequence -> earlier insertion (FIFO)
        }

        public int CompareTo(PriorityKey other)
        {
            var pCompare = InvertedPriority.CompareTo(other.InvertedPriority);
            if (pCompare != 0) return pCompare;
            return SequenceNumber.CompareTo(other.SequenceNumber);
        }
    }

    private readonly PriorityQueue<GenerationWorkItem, PriorityKey> _priorityQueue = new();
    private readonly HashSet<Guid> _enqueuedRequestIds = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _syncLock = new();
    private readonly ILogger<GenerationQueue> _logger;
    private readonly int _capacity;
    private long _sequenceCounter = 0;
    private bool _isDisposed = false;

    public int CurrentDepth
    {
        get
        {
            lock (_syncLock)
            {
                return _priorityQueue.Count;
            }
        }
    }

    public int Capacity => _capacity;

    public GenerationQueue(ILogger<GenerationQueue> logger, int capacity = 100)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Queue capacity must be greater than zero.");

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _capacity = capacity;
    }

    public ValueTask EnqueueAsync(GenerationWorkItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item, nameof(item));

        var reqId = item.Payload.GenerationRequestId;

        lock (_syncLock)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(GenerationQueue));

            // 1. Duplicate suppression
            if (_enqueuedRequestIds.Contains(reqId))
            {
                _logger.LogInformation("[GenerationQueueDuplicateSkipped] GenerationRequestId={RequestId} is already in the queue. Skipping duplicate enqueue.", reqId);
                return ValueTask.CompletedTask;
            }

            // 2. Atomic backpressure capacity check
            if (_priorityQueue.Count >= _capacity)
            {
                _logger.LogWarning("[GenerationQueueBackpressure] Queue capacity {Capacity} reached. Rejecting RequestId={RequestId}.", _capacity, reqId);
                throw new InvalidOperationException($"Generation queue capacity ({_capacity}) exceeded. Backpressure triggered.");
            }

            // 3. Enqueue with priority key
            var key = new PriorityKey(item.Priority, _sequenceCounter++);
            _priorityQueue.Enqueue(item, key);
            _enqueuedRequestIds.Add(reqId);

            _logger.LogInformation("[GenerationJobEnqueued] RequestId={RequestId}, Priority={Priority}. Current depth={Depth}/{Capacity}.",
                reqId, item.Priority, _priorityQueue.Count, _capacity);

            GenerationObservability.JobsTotal.Add(1);
        }

        // Signal waiting consumers
        _signal.Release();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<GenerationWorkItem?> DequeueAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(ct);

            lock (_syncLock)
            {
                if (_isDisposed)
                    return null;

                if (_priorityQueue.TryDequeue(out var item, out _))
                {
                    _enqueuedRequestIds.Remove(item.Payload.GenerationRequestId);
                    _logger.LogInformation("[GenerationJobDequeued] RequestId={RequestId}, Priority={Priority}. Remaining depth={Depth}.",
                        item.Payload.GenerationRequestId, item.Priority, _priorityQueue.Count);

                    var waitMs = (DateTime.UtcNow - item.EnqueuedAt).TotalMilliseconds;
                    if (waitMs >= 0)
                    {
                        GenerationObservability.QueueWaitDurationMs.Record(waitMs);
                    }

                    return item;
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _priorityQueue.Clear();
            _enqueuedRequestIds.Clear();
        }
        _signal.Dispose();
    }
}
