using Application.Common;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Dedicated staging abstraction strictly reserved for top-level workflow transaction owners
/// (e.g. ActivityExecutionService, CharacterReactionExecutionService).
/// 
/// ARCHITECTURAL INVARIANT:
/// - StageTransition is an INTERNAL STAGING PRIMITIVE, not a standalone business operation.
/// - The caller MUST explicitly own and manage the DbContext transaction lifecycle
///   (BeginTransactionAsync -> SaveChangesAsync -> CommitAsync / RollbackAsync).
/// - It stages the state delta mutation onto the tracked CharacterState and appends
///   the CharacterStateTransition ledger entry into the CoreDbContext change tracker.
/// - Do NOT call this method outside of an outer workflow transaction.
/// </summary>
public interface ICharacterStateTransitionStager
{
    /// <summary>
    /// Stages a state transition onto the entity and CoreDbContext change tracker without committing.
    /// Enables atomic commit with parent source operations (Reaction / Activity) inside caller's transaction.
    /// </summary>
    StateTransitionResult StageTransition(
        CharacterState state,
        CharacterStateDelta delta,
        StateTransitionContext context,
        DateTime nowUtc);
}

/// <summary>
/// Canonical business operation service for persistent character state transitions.
/// Manages standalone state transitions with self-contained atomic transaction semantics.
/// Callers requiring internal staging within an outer transaction must use ICharacterStateTransitionStager.
/// </summary>
public interface ICharacterStateTransitionService
{
    /// <summary>
    /// Executes a standalone state transition atomically.
    /// Invariant:
    /// - If an ambient transaction exists (CurrentTransaction != null), participates in that transaction without premature commits.
    /// - If no outer transaction exists, establishes and commits its own dedicated database transaction.
    /// </summary>
    Task<StateTransitionResult> TransitionAsync(
        Guid characterId,
        CharacterStateDelta delta,
        StateTransitionContext context,
        DateTime nowUtc,
        CancellationToken ct = default);
}
