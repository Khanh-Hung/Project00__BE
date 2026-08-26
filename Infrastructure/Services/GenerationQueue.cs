using Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Infrastructure.Services;

/// <summary>
/// Thread-safe in-memory bounded queue implementation for generation jobs.
/// Provides capacity limits (backpressure), duplicate enqueue suppression, and FIFO delivery.
/// </summary>
public sealed class GenerationQueue : IGenerationJobQueue
{
    private readonly Channel<Guid> _channel;
    private readonly ConcurrentDictionary<Guid, byte> _enqueuedJobIds = new();
    private readonly ILogger<GenerationQueue> _logger;
    private readonly int _capacity;

    public int CurrentDepth => _enqueuedJobIds.Count;
    public int Capacity => _capacity;

    public GenerationQueue(ILogger<GenerationQueue> logger, int capacity = 100)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Queue capacity must be greater than zero.");

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _capacity = capacity;

        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<Guid>(options);
    }

    public async ValueTask EnqueueAsync(Guid jobId, int priority = 0, CancellationToken ct = default)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("JobId cannot be empty.", nameof(jobId));

        if (CurrentDepth >= _capacity)
        {
            _logger.LogWarning("[GenerationQueueBackpressure] Queue capacity {Capacity} reached. Rejecting JobId={JobId}.", _capacity, jobId);
            throw new InvalidOperationException($"Generation queue capacity ({_capacity}) exceeded. Backpressure triggered.");
        }

        // Duplicate suppression: if already in queue, skip re-enqueuing
        if (!_enqueuedJobIds.TryAdd(jobId, 0))
        {
            _logger.LogInformation("[GenerationQueueDuplicateSkipped] JobId={JobId} is already in the queue. Skipping duplicate enqueue.", jobId);
            return;
        }

        try
        {
            await _channel.Writer.WriteAsync(jobId, ct);
            _logger.LogInformation("[GenerationJobEnqueued] JobId={JobId} enqueued. Current depth={Depth}/{Capacity}.", jobId, CurrentDepth, _capacity);
        }
        catch
        {
            _enqueuedJobIds.TryRemove(jobId, out _);
            throw;
        }
    }

    public async ValueTask<Guid?> DequeueAsync(CancellationToken ct = default)
    {
        if (await _channel.Reader.WaitToReadAsync(ct))
        {
            if (_channel.Reader.TryRead(out var jobId))
            {
                _enqueuedJobIds.TryRemove(jobId, out _);
                _logger.LogInformation("[GenerationJobDequeued] JobId={JobId} dequeued. Remaining depth={Depth}.", jobId, CurrentDepth);
                return jobId;
            }
        }

        return null;
    }
}
