using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.ValueObjects;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Application service responsible for coordinating relationship feedback from a completed cognitive cycle.
/// Invariant: Must NEVER mutate CharacterState.
/// Invariant: Non-fatal persistence failure must not roll back committed CharacterState or action execution.
/// </summary>
public interface ICharacterRelationshipFeedbackService
{
    Task<CharacterRelationshipFeedback?> RecordFeedbackAsync(
        CharacterCognitiveCycleContext cycleContext,
        CharacterCognitiveCycleResult cycleResult,
        CancellationToken ct = default);
}
