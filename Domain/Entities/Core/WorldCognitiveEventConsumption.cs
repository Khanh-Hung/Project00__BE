using System;
using Domain.Common;
using Domain.Enums;
using Domain.Exceptions;

namespace Domain.Entities;

/// <summary>
/// Authoritative domain aggregate tracking factual world event consumption within the cognitive boundary.
/// Enforces idempotent single-dispatch, deterministic crash recovery, and tracks terminal status.
/// Invariant: Strictly factual event envelope data. Never contains CharacterState, needs, emotions,
/// relationships, or cognitive interpretations.
/// </summary>
public sealed class WorldCognitiveEventConsumption
{
    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid CharacterId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public string EventName { get; private set; } = string.Empty;
    public string Source { get; private set; } = string.Empty;
    public string? Category { get; private set; }
    public string Fingerprint { get; private set; } = string.Empty;
    public Guid? CycleId { get; private set; }
    public EventConsumptionState State { get; private set; } = EventConsumptionState.InProgress;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }
    public DateTime LastAttemptAtUtc { get; private set; }
    public int AttemptCount { get; private set; } = 1;
    public string? FailureReason { get; private set; }
    public uint Version { get; private set; } = 1;

    private WorldCognitiveEventConsumption() { } // EF Core

    public WorldCognitiveEventConsumption(
        Guid eventId,
        Guid characterId,
        DateTime occurredAtUtc,
        string eventName,
        string source,
        string? category,
        string fingerprint,
        DateTime createdAtUtc,
        DateTime? lastAttemptAtUtc = null,
        int attemptCount = 1,
        Guid? id = null)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("EventId cannot be empty.", nameof(eventId));

        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(eventName))
            throw new ArgumentException("EventName cannot be empty or whitespace.", nameof(eventName));

        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source cannot be empty or whitespace.", nameof(source));

        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("Fingerprint cannot be empty or whitespace.", nameof(fingerprint));

        Id = id ?? Guid.NewGuid();
        EventId = eventId;
        CharacterId = characterId;
        OccurredAtUtc = occurredAtUtc;
        EventName = eventName.Trim();
        Source = source.Trim();
        Category = category?.Trim();
        Fingerprint = fingerprint.Trim();
        CreatedAtUtc = createdAtUtc;
        LastAttemptAtUtc = lastAttemptAtUtc ?? createdAtUtc;
        AttemptCount = Math.Max(1, attemptCount);
        State = EventConsumptionState.InProgress;
        Version = 1;
    }

    /// <summary>
    /// Creates a new InProgress consumption claim with canonical fingerprint computed automatically.
    /// </summary>
    public static WorldCognitiveEventConsumption CreateClaim(
        Guid eventId,
        Guid characterId,
        DateTimeOffset occurredAtUtc,
        string eventName,
        string source,
        string? category,
        DateTime createdAtUtc,
        Guid? id = null)
    {
        var fingerprint = CanonicalWorldEventFingerprint.Compute(
            eventId,
            characterId,
            occurredAtUtc,
            eventName,
            source,
            category);

        return new WorldCognitiveEventConsumption(
            eventId,
            characterId,
            occurredAtUtc.UtcDateTime,
            eventName,
            source,
            category,
            fingerprint,
            createdAtUtc,
            lastAttemptAtUtc: createdAtUtc,
            attemptCount: 1,
            id: id);
    }

    /// <summary>
    /// Computes canonical fingerprint for this consumption aggregate based on its factual fields.
    /// </summary>
    public string ComputeCanonicalFingerprint() =>
        CanonicalWorldEventFingerprint.Compute(
            EventId,
            CharacterId,
            new DateTimeOffset(OccurredAtUtc, TimeSpan.Zero),
            EventName,
            Source,
            Category);

    /// <summary>
    /// Validates that the entity's stored fingerprint matches the deterministic canonical computation.
    /// </summary>
    public void ValidateCanonicalFingerprint()
    {
        var expected = ComputeCanonicalFingerprint();
        if (!string.Equals(Fingerprint, expected, StringComparison.Ordinal))
        {
            throw new WorldCognitiveEventIdempotencyConflictException(
                EventId,
                expected,
                Fingerprint,
                $"Canonical fingerprint mismatch for EventId={EventId}. Expected '{expected}' but received '{Fingerprint}'.");
        }
    }

    /// <summary>
    /// Reclaims an event in Failed state for retry.
    /// Invariant: In PR54 (Option A), an InProgress claim cannot be reclaimed merely due to elapsed lease time.
    /// Lease expiration alone does not guarantee that the remote executing worker has died, and consumption record
    /// fencing cannot prevent duplicate CharacterState mutations during ActionExecution.
    /// Invariant: A Consumed event is terminal and can never be reclaimed.
    /// </summary>
    public void Reclaim(DateTime attemptedAtUtc, TimeSpan? leaseTimeout = null)
    {
        if (State == EventConsumptionState.Consumed)
            throw new InvalidOperationException($"Cannot reclaim an already Consumed event (EventId: {EventId:D}).");

        if (State == EventConsumptionState.InProgress)
        {
            throw new InvalidOperationException(
                $"Cannot reclaim an InProgress claim for EventId '{EventId:D}'. In PR54, lease expiration alone does not guarantee worker termination and cannot fence CharacterState side effects. InProgress claims cannot be reclaimed without explicit terminal failure.");
        }

        State = EventConsumptionState.InProgress;
        LastAttemptAtUtc = attemptedAtUtc;
        AttemptCount++;
        FailureReason = null;
        Version++;
    }

    /// <summary>
    /// Transitions consumption claim to Consumed state upon successful Cognitive Cycle execution.
    /// Invariant: CycleId must be valid and distinct from EventId.
    /// Invariant: Stale workers whose claim version has advanced are fenced out.
    /// </summary>
    public void MarkConsumed(Guid cycleId, DateTime consumedAtUtc, uint? expectedVersion = null)
    {
        if (cycleId == Guid.Empty)
            throw new ArgumentException("CycleId cannot be empty.", nameof(cycleId));

        if (cycleId == EventId)
            throw new InvalidOperationException($"CycleId '{cycleId:D}' must be distinct from EventId.");

        if (expectedVersion.HasValue && expectedVersion.Value != Version)
        {
            throw new InvalidOperationException(
                $"Stale worker fencing violation for EventId '{EventId:D}'. Expected claim version {expectedVersion.Value} but entity version is {Version}.");
        }

        if (State == EventConsumptionState.Consumed)
            return;

        if (State != EventConsumptionState.InProgress)
            throw new InvalidOperationException($"Cannot transition consumption {Id} from {State} to Consumed. Must be InProgress.");

        CycleId = cycleId;
        ConsumedAtUtc = consumedAtUtc;
        State = EventConsumptionState.Consumed;
        FailureReason = null;
        Version++;
    }

    /// <summary>
    /// Transitions consumption claim to Failed state when cycle execution or processing fails.
    /// </summary>
    public void MarkFailed(string reason, DateTime failedAtUtc, uint? expectedVersion = null)
    {
        if (State == EventConsumptionState.Consumed)
            throw new InvalidOperationException($"Cannot mark an already Consumed event as Failed (EventId: {EventId:D}).");

        if (expectedVersion.HasValue && expectedVersion.Value != Version)
        {
            throw new InvalidOperationException(
                $"Stale worker fencing violation for EventId '{EventId:D}'. Expected claim version {expectedVersion.Value} but entity version is {Version}.");
        }

        State = EventConsumptionState.Failed;
        FailureReason = reason;
        LastAttemptAtUtc = failedAtUtc;
        Version++;
    }
}
