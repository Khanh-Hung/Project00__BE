using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;

namespace Application.Abstractions.Data;

/// <summary>
/// Repository abstraction for persisting, claiming, and tracking world cognitive event consumptions.
/// Enforces character isolation, deterministic retrieval, lease-based crash recovery, and database-level idempotency protection.
/// </summary>
public interface IWorldCognitiveEventConsumptionRepository
{
    /// <summary>
    /// Retrieves consumption record by EventId, if any exists.
    /// </summary>
    Task<WorldCognitiveEventConsumption?> GetByEventIdAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>
    /// Attempts to atomically claim consumption for the specified event.
    /// Returns (true, consumption) if newly claimed or reclaimed for retry/recovery.
    /// Returns (false, existing) if already consumed (terminal duplicate) or actively in-flight within lease window.
    /// Throws WorldCognitiveEventIdempotencyConflictException if an existing record has a divergent fingerprint.
    /// </summary>
    Task<(bool IsClaimed, WorldCognitiveEventConsumption Consumption)> TryClaimAsync(
        WorldCognitiveEventConsumption consumption,
        TimeSpan? leaseTimeout = null,
        CancellationToken ct = default);

    /// <summary>
    /// Explicitly reclaims a consumption record for retry or crash recovery.
    /// Invariant: Throws InvalidOperationException if State is Consumed or if State is InProgress within lease window.
    /// </summary>
    Task<(bool IsReclaimed, WorldCognitiveEventConsumption Consumption)> ReclaimAsync(
        Guid eventId,
        DateTime attemptedAtUtc,
        TimeSpan? leaseTimeout = null,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions consumption state to Consumed with the completed CycleId, timestamp, and fencing token.
    /// Invariant: Stale workers whose claim version has advanced are rejected via DbUpdateConcurrencyException.
    /// </summary>
    Task MarkConsumedAsync(
        Guid eventId,
        Guid cycleId,
        DateTime consumedAtUtc,
        uint? expectedVersion = null,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions consumption state to Failed with failure explanation, timestamp, and fencing token.
    /// </summary>
    Task MarkFailedAsync(
        Guid eventId,
        string failureReason,
        DateTime failedAtUtc,
        uint? expectedVersion = null,
        CancellationToken ct = default);

    /// <summary>
    /// Persists pending unit of work changes.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
