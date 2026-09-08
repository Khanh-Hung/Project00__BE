using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Time;
using Application.Contracts.ActionExecution;
using Application.Contracts.Goals;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Goals;

/// <summary>
/// Authoritative goal subsystem orchestration service.
/// Coordinates deterministic goal selection, deduplicated persistence, and idempotent progress updates.
/// </summary>
public sealed class CharacterGoalService : ICharacterGoalService
{
    private readonly ICharacterGoalRepository _goalRepository;
    private readonly ICharacterGoalPolicy _goalPolicy;
    private readonly ISystemClock _clock;
    private readonly ILogger<CharacterGoalService> _logger;

    // Process-local idempotency cache for goal progress executions
    private static readonly ConcurrentDictionary<(Guid GoalId, Guid ExecutionId), CharacterGoalProgressFeedback> _executionCache = new();

    public CharacterGoalService(
        ICharacterGoalRepository goalRepository,
        ICharacterGoalPolicy goalPolicy,
        ISystemClock clock,
        ILogger<CharacterGoalService> logger)
    {
        _goalRepository = goalRepository ?? throw new ArgumentNullException(nameof(goalRepository));
        _goalPolicy = goalPolicy ?? throw new ArgumentNullException(nameof(goalPolicy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterGoal?> GetOrSelectActiveGoalAsync(
        Guid characterId,
        CharacterDesireEvaluation desireEvaluation,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(desireEvaluation, nameof(desireEvaluation));

        // 1. Retrieve all current active goals authoritatively
        var activeGoals = await _goalRepository.GetActiveGoalsAsync(characterId, ct);

        // 2. Evaluate deterministic domain policy
        var decision = _goalPolicy.Evaluate(characterId, desireEvaluation, activeGoals, now);

        switch (decision.Action)
        {
            case GoalPolicyAction.ReuseExisting:
                return decision.SelectedGoal;

            case GoalPolicyAction.CreateNew:
                var candidate = decision.Candidate!;
                var newGoal = CharacterGoal.Create(
                    characterId: characterId,
                    goalKey: candidate.GoalKey,
                    goalType: candidate.GoalType,
                    priority: candidate.Priority,
                    description: candidate.Description,
                    initialStatus: CharacterGoalStatus.Active,
                    initialProgress: 0,
                    now: now);

                try
                {
                    await _goalRepository.AddAsync(newGoal, ct);
                    return newGoal;
                }
                catch (Exception ex) when (IsUniqueConstraintViolation(ex))
                {
                    _logger.LogInformation(
                        ex,
                        "[CharacterGoalService] Concurrent creation detected for CharacterId={CharacterId}, GoalKey={GoalKey}. Reusing winner.",
                        characterId, candidate.GoalKey);

                    var winner = await _goalRepository.GetActiveByGoalKeyAsync(characterId, candidate.GoalKey, ct);
                    return winner ?? newGoal;
                }

            case GoalPolicyAction.None:
            default:
                // Fall back to existing highest-priority active goal if any exist
                return activeGoals.FirstOrDefault();
        }
    }

    public async Task<CharacterGoalProgressFeedback?> ApplyProgressFeedbackAsync(
        Guid characterId,
        Guid executionId,
        CharacterActionExecutionResult actionExecution,
        CharacterGoalContext? goalContext,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actionExecution, nameof(actionExecution));

        if (goalContext == null || actionExecution.Status != CharacterActionExecutionStatus.Applied)
        {
            return null;
        }

        // Idempotency check: reuse previously recorded progress feedback for this ExecutionId
        var cacheKey = (goalContext.GoalId, executionId);
        if (_executionCache.TryGetValue(cacheKey, out var existingFeedback))
        {
            return existingFeedback with { IsDuplicateExecution = true };
        }

        var goal = await _goalRepository.GetByIdAsync(goalContext.GoalId, ct);
        if (goal == null)
        {
            _logger.LogWarning(
                "[CharacterGoalService] Goal {GoalId} not found when applying progress feedback.",
                goalContext.GoalId);
            return null;
        }

        if (goal.CharacterId != characterId)
        {
            _logger.LogWarning(
                "[CharacterGoalService] Goal {GoalId} character mismatch (Expected={Expected}, Actual={Actual}).",
                goalContext.GoalId, characterId, goal.CharacterId);
            return null;
        }

        if (goal.Status == CharacterGoalStatus.Completed)
        {
            throw new InvalidOperationException($"Completed goal '{goal.Id}' cannot receive further progress.");
        }

        if (goal.Status != CharacterGoalStatus.Active)
        {
            return null;
        }

        int previousProgress = (int)goal.Progress;
        int increment = 25; // Standard bounded progress step
        int targetProgress = Math.Min(100, previousProgress + increment);

        goal.UpdateProgress(targetProgress, now);
        await _goalRepository.UpdateAsync(goal, ct);

        var feedback = new CharacterGoalProgressFeedback(
            GoalId: goal.Id,
            ExecutionId: executionId,
            PreviousProgress: previousProgress,
            NewProgress: (int)goal.Progress,
            Status: goal.Status,
            IsDuplicateExecution: false
        );

        _executionCache.TryAdd(cacheKey, feedback);
        return feedback;
    }

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        if (ex is DbUpdateException dbEx)
        {
            var msg = dbEx.InnerException?.Message ?? dbEx.Message;
            return msg.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("23505", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("19", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
