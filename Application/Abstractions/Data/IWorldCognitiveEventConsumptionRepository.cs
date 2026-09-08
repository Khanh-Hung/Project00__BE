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
    /// </summary>
    Task<(bool IsReclaimed, WorldCognitiveEventConsumption Consumption)> ReclaimAsync(
        Guid eventId,
        DateTime attemptedAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions consumption state to Consumed with the completed CycleId and timestamp.
    /// </summary>
    Task MarkConsumedAsync(Guid eventId, Guid cycleId, DateTime consumedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Transitions consumption state to Failed with failure explanation and timestamp.
    /// </summary>
    Task MarkFailedAsync(Guid eventId, string failureReason, DateTime failedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Persists pending unit of work changes.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
