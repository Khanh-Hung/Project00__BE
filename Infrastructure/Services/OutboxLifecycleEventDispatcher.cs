using Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Default implementation of IOutboxLifecycleEventDispatcher that logs and dispatches outbox domain events.
/// Can be decorated or replaced with message brokers (RabbitMQ, Kafka, SignalR, etc.) in production.
/// </summary>
public sealed class OutboxLifecycleEventDispatcher : IOutboxLifecycleEventDispatcher
{
    private readonly ILogger<OutboxLifecycleEventDispatcher> _logger;

    public OutboxLifecycleEventDispatcher(ILogger<OutboxLifecycleEventDispatcher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task DispatchAsync(string eventType, string payloadJson, CancellationToken ct = default)
    {
        _logger.LogInformation("[OutboxLifecycleDispatched] Dispatched lifecycle domain event '{EventType}': {Payload}",
            eventType, payloadJson);
        return Task.CompletedTask;
    }
}
