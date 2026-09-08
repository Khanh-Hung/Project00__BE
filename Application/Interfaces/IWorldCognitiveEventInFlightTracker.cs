using System;
using System.Threading;

namespace Application.Interfaces;

/// <summary>
/// Execution scope held while a WorldCognitiveEvent is actively processed in-flight.
/// Provides linked cancellation so that any explicit reclaim or invalidation can abort
/// active execution before business side effects (such as CharacterState mutation) occur.
/// </summary>
public interface IWorldCognitiveEventExecutionScope : IDisposable
{
    Guid EventId { get; }
    CancellationToken CancellationToken { get; }
    bool IsStale { get; }
    void Cancel();
}

/// <summary>
/// Process-local boundary tracker preventing duplicate cognitive cycle execution
/// and managing in-flight execution lifecycle for WorldCognitiveEvent consumption.
/// Note: Process-local guard only. Distributed ownership and recovery are enforced
/// by durable database constraints and fencing tokens.
/// </summary>
public interface IWorldCognitiveEventInFlightTracker
{
    /// <summary>
    /// Attempts to register active execution for the given EventId BEFORE durable claiming starts.
    /// Returns true and an execution scope handle if registration succeeded.
    /// Returns false if an execution for this EventId is already active in-flight in this process.
    /// </summary>
    bool TryTrack(Guid eventId, out IWorldCognitiveEventExecutionScope? scope);

    /// <summary>
    /// Checks whether an execution for the given EventId is currently active in-flight in this process.
    /// </summary>
    bool IsInFlight(Guid eventId);

    /// <summary>
    /// Explicitly invalidates and cancels any active in-flight execution for the given EventId.
    /// Used by recovery boundaries to ensure stale workers cannot commit business side effects.
    /// </summary>
    void Invalidate(Guid eventId);
}
