using System;
using System.Collections.Concurrent;
using System.Threading;
using Application.Interfaces;

namespace Infrastructure.Services.CognitiveCycle;

/// <summary>
/// Thread-safe in-memory tracker preventing duplicate cognitive cycle execution
/// for the same WorldCognitiveEvent across all consumers in the application process.
/// Invariant: Process-local execution guard only. Not a distributed lock.
/// </summary>
public sealed class WorldCognitiveEventInFlightTracker : IWorldCognitiveEventInFlightTracker
{
    private static readonly Lazy<WorldCognitiveEventInFlightTracker> LazyInstance =
        new(() => new WorldCognitiveEventInFlightTracker());

    public static WorldCognitiveEventInFlightTracker Instance => LazyInstance.Value;

    private readonly ConcurrentDictionary<Guid, InFlightExecutionScope> _inFlightScopes = new();

    public bool TryTrack(Guid eventId, out IWorldCognitiveEventExecutionScope? scope)
    {
        var candidate = new InFlightExecutionScope(this, eventId);
        if (_inFlightScopes.TryAdd(eventId, candidate))
        {
            scope = candidate;
            return true;
        }

        candidate.Dispose();
        scope = null;
        return false;
    }

    public bool IsInFlight(Guid eventId) => _inFlightScopes.ContainsKey(eventId);

    public void Invalidate(Guid eventId)
    {
        if (_inFlightScopes.TryGetValue(eventId, out var existingScope))
        {
            existingScope.Cancel();
        }
    }

    private void Remove(Guid eventId)
    {
        _inFlightScopes.TryRemove(eventId, out _);
    }

    private sealed class InFlightExecutionScope : IWorldCognitiveEventExecutionScope
    {
        private readonly WorldCognitiveEventInFlightTracker _tracker;
        private readonly CancellationTokenSource _cts = new();
        private int _disposed;
        private int _cancelled;

        public Guid EventId { get; }
        public CancellationToken CancellationToken => _cts.Token;
        public bool IsStale => Volatile.Read(ref _cancelled) == 1;

        public InFlightExecutionScope(WorldCognitiveEventInFlightTracker tracker, Guid eventId)
        {
            _tracker = tracker;
            EventId = eventId;
        }

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore if already disposed
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _tracker.Remove(EventId);
                _cts.Dispose();
            }
        }
    }
}
