using System;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Authoritative domain aggregate representing a durable outbox record for factual world and life simulation events.
/// Strictly decoupled from cognitive interpretations, emotions, and psychological states.
/// Invariants:
/// - EventId != Id (OutboxMessageId)
/// - EventId uniqueness is enforced at database level
/// - Status transitions are protected against invalid transitions
/// - Optimistic concurrency token (Version) protects concurrent modifications
/// </summary>
public sealed class CharacterOutboxMessage
{
    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid CharacterId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public string Fingerprint { get; private set; } = string.Empty;
    public DateTime OccurredAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public CharacterOutboxStatus Status { get; private set; } = CharacterOutboxStatus.Pending;
    public int AttemptCount { get; private set; }
    public int MaxRetries { get; private set; } = 3;
    public DateTime? ProcessedAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public uint Version { get; private set; } = 1;

    private CharacterOutboxMessage() { } // EF Core

    public CharacterOutboxMessage(
        Guid eventId,
        Guid characterId,
        string eventType,
        string payloadJson,
        string fingerprint,
        DateTime occurredAtUtc,
        DateTime? createdAtUtc = null,
        CharacterOutboxStatus status = CharacterOutboxStatus.Pending,
        int attemptCount = 0,
        int maxRetries = 3,
        DateTime? processedAtUtc = null,
        string? lastError = null,
        uint version = 1,
        Guid? id = null)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("EventId cannot be empty.", nameof(eventId));

        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("EventType cannot be empty or whitespace.", nameof(eventType));

        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("PayloadJson cannot be empty or whitespace.", nameof(payloadJson));

        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("Fingerprint cannot be empty or whitespace.", nameof(fingerprint));

        Id = id ?? Guid.NewGuid();
        EventId = eventId;
        CharacterId = characterId;
        EventType = eventType.Trim();
        PayloadJson = payloadJson;
        Fingerprint = fingerprint.Trim();
        OccurredAtUtc = occurredAtUtc;
        CreatedAtUtc = createdAtUtc ?? occurredAtUtc;
        Status = status;
        AttemptCount = attemptCount;
        MaxRetries = maxRetries;
        ProcessedAtUtc = processedAtUtc;
        LastError = lastError;
        Version = version == 0 ? 1u : version;
    }

    /// <summary>
    /// Marks the outbox message as currently being processed.
    /// Transition: Pending -> Processing.
    /// </summary>
    public void MarkProcessing()
    {
        if (Status == CharacterOutboxStatus.Published)
            throw new InvalidOperationException($"Cannot transition message {Id} to Processing because it is already Published.");

        if (Status == CharacterOutboxStatus.Processing)
            throw new InvalidOperationException($"Message {Id} is already in Processing state.");

        Status = CharacterOutboxStatus.Processing;
        Version++;
    }

    /// <summary>
    /// Marks the outbox message as successfully published/dispatched.
    /// Transition: Processing or Pending -> Published.
    /// </summary>
    public void MarkPublished(DateTime processedAtUtc)
    {
        if (Status == CharacterOutboxStatus.Published)
            return;

        if (Status == CharacterOutboxStatus.Failed)
            throw new InvalidOperationException($"Cannot transition failed message {Id} directly to Published. It must be retried first.");

        Status = CharacterOutboxStatus.Published;
        ProcessedAtUtc = processedAtUtc;
        LastError = null;
        Version++;
    }

    /// <summary>
    /// Marks the outbox message as failed due to an error during dispatch.
    /// Transition: Processing or Pending -> Failed (or Pending for retry).
    /// </summary>
    public void MarkFailed(string error, DateTime failedAtUtc, bool canRetry = true)
    {
        if (Status == CharacterOutboxStatus.Published)
            throw new InvalidOperationException($"Cannot mark a Published message as Failed (MessageId: {Id}).");

        AttemptCount++;
        LastError = error;

        if (canRetry && AttemptCount < MaxRetries)
        {
            // Transition back to Pending for subsequent retry
            Status = CharacterOutboxStatus.Pending;
        }
        else
        {
            Status = CharacterOutboxStatus.Failed;
            ProcessedAtUtc = failedAtUtc;
        }

        Version++;
    }

    /// <summary>
    /// Explicitly resets a failed message back to Pending for manual or automated recovery.
    /// Invariant: Published messages can NEVER be reset.
    /// </summary>
    public void Retry()
    {
        if (Status == CharacterOutboxStatus.Published)
            throw new InvalidOperationException($"Published messages cannot be retried (MessageId: {Id}).");

        Status = CharacterOutboxStatus.Pending;
        Version++;
    }
}
