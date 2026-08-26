namespace Application.Interfaces;

/// <summary>
/// Dispatches outbox lifecycle domain events (e.g. attempt started, attempt evaluated, job accepted, job quarantined) to external subscribers/brokers.
/// </summary>
public interface IOutboxLifecycleEventDispatcher
{
    Task DispatchAsync(string eventType, string payloadJson, CancellationToken ct = default);
}
