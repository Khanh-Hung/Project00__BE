using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;

namespace Application.Contracts.LifeSimulation;

/// <summary>
/// Factual JSON payload structure for Life Simulation events persisted into the Outbox.
/// Invariant: Strictly contains factual event data. Never contains CharacterState,
/// psychological needs, emotional metrics, relationship metrics, or cognitive interpretations.
/// </summary>
public sealed record CharacterOutboxPayload
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("eventId")]
    public Guid EventId { get; init; }

    [JsonPropertyName("characterId")]
    public Guid CharacterId { get; init; }

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("activityId")]
    public Guid ActivityId { get; init; }

    [JsonPropertyName("activityType")]
    public LifeActivityType ActivityType { get; init; }

    [JsonPropertyName("occurredAtUtc")]
    public DateTimeOffset OccurredAtUtc { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    public string ToJson() => JsonSerializer.Serialize(this);

    public static CharacterOutboxPayload? FromJson(string json) => JsonSerializer.Deserialize<CharacterOutboxPayload>(json);

    public static CharacterOutboxMessage CreateOutboxMessage(LifeSimulationEvent simEvent, DateTime? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(simEvent);

        var payload = new CharacterOutboxPayload
        {
            SchemaVersion = 1,
            EventId = simEvent.EventId,
            CharacterId = simEvent.CharacterId,
            EventType = simEvent.EventType,
            ActivityId = simEvent.ActivityId,
            ActivityType = simEvent.ActivityType,
            OccurredAtUtc = simEvent.OccurredAtUtc,
            Description = simEvent.Description
        };

        var fingerprint = CanonicalOutboxFingerprint.Compute(
            simEvent.EventId,
            simEvent.CharacterId,
            simEvent.EventType,
            simEvent.ActivityId,
            simEvent.ActivityType,
            simEvent.OccurredAtUtc.UtcDateTime
        );

        return new CharacterOutboxMessage(
            eventId: simEvent.EventId,
            characterId: simEvent.CharacterId,
            eventType: simEvent.EventType,
            payloadJson: payload.ToJson(),
            fingerprint: fingerprint,
            occurredAtUtc: simEvent.OccurredAtUtc.UtcDateTime,
            createdAtUtc: createdAtUtc
        );
    }

    public static string ComputeCanonicalFingerprint(
        Guid eventId,
        Guid characterId,
        string eventType,
        string payloadJson,
        DateTime occurredAtUtc)
    {
        CharacterOutboxPayload? payload = null;
        try
        {
            payload = FromJson(payloadJson);
        }
        catch (Exception ex)
        {
            throw new CharacterOutboxIdempotencyConflictException(
                eventId,
                string.Empty,
                string.Empty,
                $"Cannot compute canonical fingerprint because payloadJson for EventId={eventId} is structurally invalid: {ex.Message}");
        }

        if (payload == null)
        {
            throw new CharacterOutboxIdempotencyConflictException(
                eventId,
                string.Empty,
                string.Empty,
                $"Cannot compute canonical fingerprint because payloadJson for EventId={eventId} is null or empty.");
        }

        // Schema version invariant
        if (payload.SchemaVersion != 1)
        {
            throw new CharacterOutboxIdempotencyConflictException(
                eventId,
                string.Empty,
                string.Empty,
                $"Payload SchemaVersion={payload.SchemaVersion} is unsupported for EventId={eventId}. Only schema version 1 is supported.");
        }

        // Factual metadata consistency invariants between message envelope and payload
        if (payload.EventId != eventId)
        {
            throw new CharacterOutboxIdempotencyConflictException(
                eventId,
                string.Empty,
                string.Empty,
                $"Payload EventId '{payload.EventId}' does not match message envelope EventId '{eventId}'.");
        }

        if (payload.CharacterId != characterId)
        {
            throw new CharacterOutboxIdempotencyConflictException(
                eventId,
                string.Empty,
                string.Empty,
                $"Payload CharacterId '{payload.CharacterId}' does not match message envelope CharacterId '{characterId}'.");
        }

        if (string.IsNullOrWhiteSpace(eventType) || !string.Equals(payload.EventType?.Trim(), eventType.Trim(), StringComparison.Ordinal))
        {
            throw new CharacterOutboxIdempotencyConflictException(
                eventId,
                string.Empty,
                string.Empty,
                $"Payload EventType '{payload.EventType}' does not match message envelope EventType '{eventType}'.");
        }

        if (payload.OccurredAtUtc.UtcDateTime != occurredAtUtc)
        {
            throw new CharacterOutboxIdempotencyConflictException(
                eventId,
                string.Empty,
                string.Empty,
                $"Payload OccurredAtUtc '{payload.OccurredAtUtc.UtcDateTime:O}' does not match message envelope OccurredAtUtc '{occurredAtUtc:O}'.");
        }

        return CanonicalOutboxFingerprint.Compute(
            eventId,
            characterId,
            eventType.Trim(),
            payload.ActivityId,
            payload.ActivityType,
            occurredAtUtc);
    }

    public static string ComputeCanonicalFingerprint(CharacterOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ComputeCanonicalFingerprint(
            message.EventId,
            message.CharacterId,
            message.EventType,
            message.PayloadJson,
            message.OccurredAtUtc);
    }

    public static void ValidateCanonicalFingerprint(CharacterOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var expectedFingerprint = ComputeCanonicalFingerprint(message);
        if (!string.Equals(message.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new CharacterOutboxIdempotencyConflictException(
                message.EventId,
                expectedFingerprint,
                message.Fingerprint,
                $"Canonical fingerprint mismatch for EventId={message.EventId}. Expected canonical fingerprint '{expectedFingerprint}' but received '{message.Fingerprint}'.");
        }
    }
}

/// <summary>
/// Thrown when an outbox message with an existing EventId is inserted or processed,
/// but its semantic fingerprint diverges from the authoritative stored fingerprint.
/// </summary>
public class CharacterOutboxIdempotencyConflictException : InvalidOperationException
{
    public Guid EventId { get; }
    public string StoredFingerprint { get; }
    public string IncomingFingerprint { get; }

    public CharacterOutboxIdempotencyConflictException(
        Guid eventId,
        string storedFingerprint,
        string incomingFingerprint,
        string? message = null)
        : base(message ?? $"Idempotency conflict detected for EventId={eventId}. Stored fingerprint '{storedFingerprint}' does not match incoming fingerprint '{incomingFingerprint}'.")
    {
        EventId = eventId;
        StoredFingerprint = storedFingerprint;
        IncomingFingerprint = incomingFingerprint;
    }
}
