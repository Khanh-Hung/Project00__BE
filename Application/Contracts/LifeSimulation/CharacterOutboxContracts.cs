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
