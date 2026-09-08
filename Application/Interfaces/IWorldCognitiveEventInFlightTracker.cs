using System;

namespace Application.Interfaces;

/// <summary>
/// Tracks active in-flight world event executions within the application boundary.
/// Prevents concurrent or duplicate cognitive cycle dispatches for the same event
/// even if the database processing lease expires during long-running execution.
/// </summary>
public interface IWorldCognitiveEventInFlightTracker
{
    /// <summary>
    /// Attempts to register an active in-flight execution for the given EventId.
    /// Returns true and an IDisposable handle if registration succeeded.
    /// Returns false if an execution for this EventId is already active in-flight.
    /// </summary>
    bool TryTrack(Guid eventId, out IDisposable? registration);

    /// <summary>
    /// Checks whether an execution for the given EventId is currently active in-flight.
    /// </summary>
    bool IsInFlight(Guid eventId);
}
