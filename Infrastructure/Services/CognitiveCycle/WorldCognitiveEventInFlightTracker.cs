using System;
using System.Collections.Concurrent;
using System.Threading;
using Application.Interfaces;

namespace Infrastructure.Services.CognitiveCycle;

/// <summary>
/// Thread-safe in-memory tracker preventing duplicate cognitive cycle execution
/// for the same WorldCognitiveEvent across all consumers in the application process.
/// </summary>
public sealed class WorldCognitiveEventInFlightTracker : IWorldCognitiveEventInFlightTracker
{
    private static readonly Lazy<WorldCognitiveEventInFlightTracker> LazyInstance =
        new(() => new WorldCognitiveEventInFlightTracker());

    public static WorldCognitiveEventInFlightTracker Instance => LazyInstance.Value;

    private readonly ConcurrentDictionary<Guid, byte> _inFlightEvents = new();

    public bool TryTrack(Guid eventId, out IDisposable? registration)
    {
        if (_inFlightEvents.TryAdd(eventId, 0))
        {
            registration = new InFlightRegistration(this, eventId);
            return true;
        }

        registration = null;
        return false;
    }

    public bool IsInFlight(Guid eventId) => _inFlightEvents.ContainsKey(eventId);

    private void Remove(Guid eventId)
    {
        _inFlightEvents.TryRemove(eventId, out _);
    }

    private sealed class InFlightRegistration : IDisposable
    {
        private readonly WorldCognitiveEventInFlightTracker _tracker;
        private readonly Guid _eventId;
        private int _disposed;

        public InFlightRegistration(WorldCognitiveEventInFlightTracker tracker, Guid eventId)
        {
            _tracker = tracker;
            _eventId = eventId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _tracker.Remove(_eventId);
            }
        }
    }
}
