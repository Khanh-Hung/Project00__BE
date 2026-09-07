using System;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Exception thrown when personality adaptation persistence detects that the specified ExecutionId
/// was already recorded with different semantic payload/evidence for the character.
/// </summary>
public sealed class PersonalityAdaptationIdempotencyConflictException : InvalidOperationException
{
    public PersonalityAdaptationIdempotencyConflictException(string message) : base(message) { }
    public PersonalityAdaptationIdempotencyConflictException(string message, Exception inner) : base(message, inner) { }
}
