using System;

namespace Domain.Exceptions;

/// <summary>
/// Domain exception thrown when an incoming Goal progress execution shares an (GoalId, ExecutionId)
/// with an existing execution record, but differs in its canonical semantic payload or fingerprint.
/// </summary>
public class CharacterGoalIdempotencyConflictException : Exception
{
    public Guid GoalId { get; }
    public Guid ExecutionId { get; }
    public string? ExistingFingerprint { get; }
    public string? IncomingFingerprint { get; }

    public CharacterGoalIdempotencyConflictException(string message) : base(message)
    {
    }

    public CharacterGoalIdempotencyConflictException(
        Guid goalId,
        Guid executionId,
        string? existingFingerprint,
        string? incomingFingerprint,
        string message)
        : base(message)
    {
        GoalId = goalId;
        ExecutionId = executionId;
        ExistingFingerprint = existingFingerprint;
        IncomingFingerprint = incomingFingerprint;
    }
}
