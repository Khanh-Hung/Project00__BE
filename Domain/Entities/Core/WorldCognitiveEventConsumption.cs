using System;
using Domain.Common;
using Domain.Enums;
using Domain.Exceptions;

namespace Domain.Entities;

/// <summary>
/// Authoritative domain aggregate tracking factual world event consumption within the cognitive boundary.
/// Enforces idempotent single-dispatch and tracks terminal status of cognitive cycle execution.
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
            id);
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
    /// Transitions consumption claim to Consumed state upon successful Cognitive Cycle execution.
    /// Invariant: CycleId must be valid and distinct from EventId.
    /// </summary>
    public void MarkConsumed(Guid cycleId, DateTime consumedAtUtc)
    {
        if (cycleId == Guid.Empty)
            throw new ArgumentException("CycleId cannot be empty.", nameof(cycleId));

        if (cycleId == EventId)
            throw new InvalidOperationException($"CycleId '{cycleId:D}' must be distinct from EventId.");

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
    public void MarkFailed(string reason)
    {
        if (State == EventConsumptionState.Consumed)
            throw new InvalidOperationException($"Cannot mark an already Consumed event as Failed (EventId: {EventId:D}).");

        State = EventConsumptionState.Failed;
        FailureReason = reason;
        Version++;
    }
}
