using System;

namespace Domain.Exceptions;

/// <summary>
/// Domain exception thrown when an incoming WorldCognitiveEvent shares an EventId with an existing
/// consumption record, but differs in its canonical semantic payload or fingerprint.
/// </summary>
public class WorldCognitiveEventIdempotencyConflictException : Exception
{
    public Guid EventId { get; }
    public string ExistingFingerprint { get; }
    public string IncomingFingerprint { get; }

    public WorldCognitiveEventIdempotencyConflictException(
        Guid eventId,
        string existingFingerprint,
        string incomingFingerprint,
        string message)
        : base(message)
    {
        EventId = eventId;
        ExistingFingerprint = existingFingerprint;
        IncomingFingerprint = incomingFingerprint;
    }

    public WorldCognitiveEventIdempotencyConflictException(
        Guid eventId,
        string existingFingerprint,
        string incomingFingerprint)
        : this(
            eventId,
            existingFingerprint,
            incomingFingerprint,
            $"Idempotency conflict detected for EventId '{eventId:D}'. Existing fingerprint '{existingFingerprint}' does not match incoming fingerprint '{incomingFingerprint}'.")
    {
    }
}
