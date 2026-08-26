using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GenerationReliability;

public sealed class GenerationQueueTests
{
    [Fact]
    public async Task EnqueueAndDequeue_MaintainsFIFOOrder()
    {
        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, capacity: 10);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        await queue.EnqueueAsync(id1);
        await queue.EnqueueAsync(id2);
        await queue.EnqueueAsync(id3);

        Assert.Equal(3, queue.CurrentDepth);

        var deq1 = await queue.DequeueAsync();
        var deq2 = await queue.DequeueAsync();
        var deq3 = await queue.DequeueAsync();

        Assert.Equal(id1, deq1);
        Assert.Equal(id2, deq2);
        Assert.Equal(id3, deq3);
        Assert.Equal(0, queue.CurrentDepth);
    }

    [Fact]
    public async Task DuplicateEnqueue_SuppressedGracefully()
    {
        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, capacity: 10);

        var id = Guid.NewGuid();

        await queue.EnqueueAsync(id);
        await queue.EnqueueAsync(id); // Duplicate enqueue
        await queue.EnqueueAsync(id); // Duplicate enqueue

        Assert.Equal(1, queue.CurrentDepth);

        var deq = await queue.DequeueAsync();
        Assert.Equal(id, deq);
        Assert.Equal(0, queue.CurrentDepth);
    }

    [Fact]
    public async Task Enqueue_WhenCapacityExceeded_ThrowsInvalidOperationException_Backpressure()
    {
        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, capacity: 2);

        await queue.EnqueueAsync(Guid.NewGuid());
        await queue.EnqueueAsync(Guid.NewGuid());

        Assert.Equal(2, queue.CurrentDepth);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(Guid.NewGuid()).AsTask());
    }
}
