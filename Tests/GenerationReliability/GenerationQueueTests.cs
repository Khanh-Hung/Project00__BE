using Application.DTOs;
using Domain.ValueObjects;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GenerationReliability;

public sealed class GenerationQueueTests
{
    private static GenerationWorkItem CreateTestWorkItem(int priority = 0, Guid? requestId = null)
    {
        var reqId = requestId ?? Guid.NewGuid();
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("active scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: snapshot.TurnId,
            CharacterId: snapshot.CharacterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: reqId
        );

        return new GenerationWorkItem(payload, Guid.NewGuid(), DateTime.UtcNow, priority);
    }

    [Fact]
    public async Task EnqueueAndDequeue_WithPriority_DequeuesHigherPriorityFirst()
    {
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, capacity: 10);

        var itemLow = CreateTestWorkItem(priority: 1);
        var itemHigh = CreateTestWorkItem(priority: 10);
        var itemMedium = CreateTestWorkItem(priority: 5);

        await queue.EnqueueAsync(itemLow);
        await queue.EnqueueAsync(itemHigh);
        await queue.EnqueueAsync(itemMedium);

        Assert.Equal(3, queue.CurrentDepth);

        var deq1 = await queue.DequeueAsync();
        var deq2 = await queue.DequeueAsync();
        var deq3 = await queue.DequeueAsync();

        Assert.Equal(itemHigh.Payload.GenerationRequestId, deq1?.Payload.GenerationRequestId);
        Assert.Equal(itemMedium.Payload.GenerationRequestId, deq2?.Payload.GenerationRequestId);
        Assert.Equal(itemLow.Payload.GenerationRequestId, deq3?.Payload.GenerationRequestId);
        Assert.Equal(0, queue.CurrentDepth);
    }

    [Fact]
    public async Task EnqueueAndDequeue_SamePriority_MaintainsFIFOOrder()
    {
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, capacity: 10);

        var item1 = CreateTestWorkItem(priority: 5);
        var item2 = CreateTestWorkItem(priority: 5);
        var item3 = CreateTestWorkItem(priority: 5);

        await queue.EnqueueAsync(item1);
        await queue.EnqueueAsync(item2);
        await queue.EnqueueAsync(item3);

        Assert.Equal(3, queue.CurrentDepth);

        var deq1 = await queue.DequeueAsync();
        var deq2 = await queue.DequeueAsync();
        var deq3 = await queue.DequeueAsync();

        Assert.Equal(item1.Payload.GenerationRequestId, deq1?.Payload.GenerationRequestId);
        Assert.Equal(item2.Payload.GenerationRequestId, deq2?.Payload.GenerationRequestId);
        Assert.Equal(item3.Payload.GenerationRequestId, deq3?.Payload.GenerationRequestId);
        Assert.Equal(0, queue.CurrentDepth);
    }

    [Fact]
    public async Task DuplicateEnqueue_SuppressedGracefully()
    {
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, capacity: 10);

        var reqId = Guid.NewGuid();
        var item1 = CreateTestWorkItem(priority: 5, requestId: reqId);
        var item2 = CreateTestWorkItem(priority: 10, requestId: reqId); // Same request ID

        await queue.EnqueueAsync(item1);
        await queue.EnqueueAsync(item2); // Duplicate enqueue suppressed

        Assert.Equal(1, queue.CurrentDepth);

        var deq = await queue.DequeueAsync();
        Assert.Equal(reqId, deq?.Payload.GenerationRequestId);
        Assert.Equal(0, queue.CurrentDepth);
    }

    [Fact]
    public async Task Enqueue_WhenCapacityExceeded_ThrowsInvalidOperationException_Backpressure()
    {
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, capacity: 2);

        await queue.EnqueueAsync(CreateTestWorkItem());
        await queue.EnqueueAsync(CreateTestWorkItem());

        Assert.Equal(2, queue.CurrentDepth);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(CreateTestWorkItem()).AsTask());
    }
}
