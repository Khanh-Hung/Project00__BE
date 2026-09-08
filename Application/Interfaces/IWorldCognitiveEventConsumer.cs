using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.CognitiveCycle;

namespace Application.Interfaces;

/// <summary>
/// Authoritative boundary for consuming factual WorldCognitiveEvent records and safely
/// dispatching them into the Cognitive Cycle pipeline.
/// Enforces character-scoped isolation, DB-backed idempotency, and identity preservation.
/// </summary>
public interface IWorldCognitiveEventConsumer
{
    /// <summary>
    /// Consumes a factual WorldCognitiveEvent, ensuring idempotency and character boundary isolation.
    /// </summary>
    Task<WorldCognitiveEventConsumptionResult> ConsumeAsync(
        WorldCognitiveEvent worldEvent,
        CancellationToken cancellationToken = default);
}
