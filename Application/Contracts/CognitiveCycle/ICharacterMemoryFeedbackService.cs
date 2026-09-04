using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.ValueObjects;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Application boundary service for persisting idempotent cognitive outcome feedback into the memory system.
/// Must never mutate CharacterState directly or roll back committed state transitions upon failure.
/// </summary>
public interface ICharacterMemoryFeedbackService
{
    Task<CharacterMemoryFeedback?> RecordFeedbackAsync(
        CharacterCognitiveCycleContext cycleContext,
        CharacterCognitiveCycleResult cycleResult,
        CancellationToken ct = default);

    Task<CharacterMemoryFeedback?> RecordFeedbackAsync(
        CharacterCognitiveCycleContext cycleContext,
        CharacterCognitiveCycleResult cycleResult,
        int importance,
        CancellationToken ct = default) => RecordFeedbackAsync(cycleContext, cycleResult, ct);
}
