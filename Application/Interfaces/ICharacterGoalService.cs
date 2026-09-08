using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Application.Contracts.Goals;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Authoritative goal subsystem orchestration service.
/// Coordinates goal retrieval, policy evaluation, deduplicated persistence, and idempotent progress updates.
/// </summary>
public interface ICharacterGoalService
{
    Task<CharacterGoal?> GetOrSelectActiveGoalAsync(
        Guid characterId,
        CharacterDesireEvaluation desireEvaluation,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<CharacterGoalProgressFeedback?> ApplyProgressFeedbackAsync(
        Guid characterId,
        Guid executionId,
        CharacterActionExecutionResult actionExecution,
        CharacterGoalContext? goalContext,
        DateTimeOffset now,
        CancellationToken ct = default);
}
