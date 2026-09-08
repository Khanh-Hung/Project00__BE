using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Time;
using Application.Contracts.ActionExecution;
using Application.Contracts.Goals;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Goals;

/// <summary>
/// Authoritative goal subsystem orchestration service.
/// Coordinates deterministic goal selection, deduplicated persistence, and durable idempotent progress updates.
/// </summary>
public sealed class CharacterGoalService : ICharacterGoalService
{
    private const int MaxConcurrencyRetries = 3;

    private readonly CoreDbContext _dbContext;
    private readonly ICharacterGoalRepository _goalRepository;
    private readonly ICharacterGoalPolicy _goalPolicy;
    private readonly ILogger<CharacterGoalService> _logger;

    public CharacterGoalService(
        CoreDbContext dbContext,
        ICharacterGoalRepository goalRepository,
        ICharacterGoalPolicy goalPolicy,
        ILogger<CharacterGoalService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _goalRepository = goalRepository ?? throw new ArgumentNullException(nameof(goalRepository));
        _goalPolicy = goalPolicy ?? throw new ArgumentNullException(nameof(goalPolicy));
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
                    now: now,
                    goalType: candidate.GoalType,
                    priority: candidate.Priority,
                    description: candidate.Description,
                    initialStatus: CharacterGoalStatus.Active,
                    initialProgress: 0);

                try
                {
                    await _goalRepository.AddAsync(newGoal, ct);
                    return newGoal;
                }
                catch (DbUpdateException ex) when (IsActiveGoalUniqueViolation(ex))
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
                // When policy determines no goal matches or threshold is unreached, return null (never arbitrary fallback)
                return null;
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

        var actionTypeStr = actionExecution.ActionType?.ToString() ?? string.Empty;

        // 1. Authoritative DB load: load genuine CharacterGoal from persistence first
        var authoritativeGoal = await _dbContext.CharacterGoals
            .FirstOrDefaultAsync(g => g.Id == goalContext.GoalId, ct);

        if (authoritativeGoal == null)
        {
            _logger.LogWarning(
                "[CharacterGoalService] Goal {GoalId} not found when applying progress feedback.",
                goalContext.GoalId);
            return null;
        }

        if (authoritativeGoal.CharacterId != characterId)
        {
            _logger.LogWarning(
                "[CharacterGoalService] Goal {GoalId} character mismatch (Expected={Expected}, Actual={Actual}).",
                goalContext.GoalId, characterId, authoritativeGoal.CharacterId);
            return null;
        }

        // 2. Invariant Guard: Reject divergent caller-injected GoalKey
        if (!string.Equals(goalContext.GoalKey, authoritativeGoal.GoalKey, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "[CharacterGoalService] Goal {GoalId} key mismatch. Caller provided '{CallerKey}' but authoritative GoalKey is '{AuthoritativeKey}'. Rejecting caller context.",
                goalContext.GoalId, goalContext.GoalKey, authoritativeGoal.GoalKey);

            throw new InvalidOperationException(
                $"GoalContext GoalKey '{goalContext.GoalKey}' does not match authoritative GoalKey '{authoritativeGoal.GoalKey}' for GoalId '{authoritativeGoal.Id}'.");
        }

        if (authoritativeGoal.Status == CharacterGoalStatus.Completed)
        {
            throw new InvalidOperationException($"Completed goal '{authoritativeGoal.Id}' cannot receive further progress.");
        }

        if (authoritativeGoal.Status != CharacterGoalStatus.Active)
        {
            return null;
        }

        // 3. Semantic alignment derived strictly from authoritative Goal entity
        int progressDelta = _goalPolicy.EvaluateActionProgress(authoritativeGoal.GoalKey, actionTypeStr);

        // 4. Durable DB Idempotency Check: query transition ledger for (GoalId, ExecutionId)
        var existingProgress = await _dbContext.CharacterGoalExecutionProgresses
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.GoalId == authoritativeGoal.Id && p.ExecutionId == executionId, ct);

        if (existingProgress != null)
        {
            var expectedFingerprint = CharacterGoalExecutionProgress.ComputeFingerprint(
                characterId,
                authoritativeGoal.Id,
                executionId,
                actionTypeStr,
                progressDelta);

            if (existingProgress.OperationFingerprint != expectedFingerprint)
            {
                _logger.LogWarning(
                    "[CharacterGoalService] Idempotency conflict for GoalId={GoalId}, ExecutionId={ExecutionId}. Existing fingerprint '{Existing}' != incoming '{Incoming}'.",
                    authoritativeGoal.Id, executionId, existingProgress.OperationFingerprint, expectedFingerprint);

                throw new CharacterGoalIdempotencyConflictException(
                    authoritativeGoal.Id,
                    executionId,
                    existingProgress.OperationFingerprint,
                    expectedFingerprint,
                    $"ExecutionId '{executionId}' has already been processed with a different semantic goal progress operation.");
            }

            _logger.LogInformation(
                "[CharacterGoalService] Idempotent duplicate progress suppressed for GoalId={GoalId}, ExecutionId={ExecutionId}. Reusing recorded feedback.",
                authoritativeGoal.Id, executionId);

            var currentGoal = await _dbContext.CharacterGoals
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == authoritativeGoal.Id, ct);

            return new CharacterGoalProgressFeedback(
                GoalId: authoritativeGoal.Id,
                ExecutionId: executionId,
                PreviousProgress: existingProgress.OldProgress,
                NewProgress: existingProgress.NewProgress,
                Status: currentGoal?.Status ?? CharacterGoalStatus.Active,
                IsDuplicateExecution: true
            );
        }

        // 5. Concurrency retry loop for applying progress to Goal aggregate and persisting audit record
        for (var attempt = 1; attempt <= MaxConcurrencyRetries; attempt++)
        {
            try
            {
                var goal = attempt == 1
                    ? authoritativeGoal
                    : await _dbContext.CharacterGoals.FirstOrDefaultAsync(g => g.Id == authoritativeGoal.Id, ct);

                if (goal == null || goal.CharacterId != characterId || goal.Status != CharacterGoalStatus.Active)
                {
                    return null;
                }

                int oldProgress = goal.ProgressPercentage;
                int newProgress = oldProgress;

                if (progressDelta > 0)
                {
                    newProgress = Math.Min(100, oldProgress + progressDelta);
                    goal.UpdateProgress(newProgress, now);
                }

                var progressRecord = new CharacterGoalExecutionProgress(
                    characterId: characterId,
                    goalId: goal.Id,
                    executionId: executionId,
                    actionType: actionTypeStr,
                    progressDelta: progressDelta,
                    oldProgress: oldProgress,
                    newProgress: newProgress,
                    appliedAtUtc: now
                );

                await _dbContext.CharacterGoalExecutionProgresses.AddAsync(progressRecord, ct);
                await _dbContext.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "[CharacterGoalService] Successfully recorded goal progress for GoalId={GoalId}, ExecutionId={ExecutionId}. Progress: {Old}->{New} (Delta={Delta}).",
                    goal.Id, executionId, oldProgress, newProgress, progressDelta);

                return new CharacterGoalProgressFeedback(
                    GoalId: goal.Id,
                    ExecutionId: executionId,
                    PreviousProgress: oldProgress,
                    NewProgress: newProgress,
                    Status: goal.Status,
                    IsDuplicateExecution: false
                );
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxConcurrencyRetries)
            {
                _logger.LogWarning(ex,
                    "[CharacterGoalService] Concurrency conflict on attempt {Attempt} for GoalId={GoalId}. Retrying...",
                    attempt, authoritativeGoal.Id);

                _dbContext.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex)
            {
                if (IsGoalProgressUniqueViolation(ex))
                {
                    _logger.LogInformation(ex,
                        "[CharacterGoalService] Concurrent execution progress detected for GoalId={GoalId}, ExecutionId={ExecutionId}. Reloading recorded result.",
                        authoritativeGoal.Id, executionId);

                    _dbContext.ChangeTracker.Clear();

                    var concurrentRecord = await _dbContext.CharacterGoalExecutionProgresses
                        .AsNoTracking()
                        .FirstOrDefaultAsync(p => p.GoalId == authoritativeGoal.Id && p.ExecutionId == executionId, ct);

                    if (concurrentRecord != null)
                    {
                        var expectedFingerprint = CharacterGoalExecutionProgress.ComputeFingerprint(
                            characterId,
                            authoritativeGoal.Id,
                            executionId,
                            actionTypeStr,
                            progressDelta);

                        if (concurrentRecord.OperationFingerprint != expectedFingerprint)
                        {
                            throw new CharacterGoalIdempotencyConflictException(
                                authoritativeGoal.Id,
                                executionId,
                                concurrentRecord.OperationFingerprint,
                                expectedFingerprint,
                                $"ExecutionId '{executionId}' has already been processed with a different semantic goal progress operation.");
                        }

                        var currentGoal = await _dbContext.CharacterGoals
                            .AsNoTracking()
                            .FirstOrDefaultAsync(g => g.Id == authoritativeGoal.Id, ct);

                        return new CharacterGoalProgressFeedback(
                            GoalId: authoritativeGoal.Id,
                            ExecutionId: executionId,
                            PreviousProgress: concurrentRecord.OldProgress,
                            NewProgress: concurrentRecord.NewProgress,
                            Status: currentGoal?.Status ?? CharacterGoalStatus.Active,
                            IsDuplicateExecution: true
                        );
                    }
                }

                throw;
            }
        }

        _logger.LogError(
            "[CharacterGoalService] Concurrency retries exhausted for GoalId={GoalId}, ExecutionId={ExecutionId}.",
            authoritativeGoal.Id, executionId);

        return null;
    }

    public static bool IsActiveGoalUniqueViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        while (inner != null)
        {
            if (inner is Npgsql.PostgresException pg)
            {
                if (pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation || pg.SqlState == "23505")
                {
                    if (string.IsNullOrWhiteSpace(pg.ConstraintName) ||
                        pg.ConstraintName.Contains("IX_CharacterGoals_CharacterId_Title", StringComparison.OrdinalIgnoreCase) ||
                        pg.ConstraintName.Contains("CharacterGoals", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            var sqlStateProp = inner.GetType().GetProperty("SqlState");
            if (sqlStateProp != null)
            {
                var sqlState = sqlStateProp.GetValue(inner)?.ToString();
                if (sqlState == "23505")
                {
                    var msg = inner.Message ?? "";
                    if (msg.Contains("IX_CharacterGoals_CharacterId_Title", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("CharacterGoals", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            var sqliteErrProp = inner.GetType().GetProperty("SqliteErrorCode");
            if (sqliteErrProp != null)
            {
                var errCode = sqliteErrProp.GetValue(inner);
                if (errCode is int code && code == 19)
                {
                    var innerMsg = inner.Message ?? "";
                    if (innerMsg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) &&
                        (innerMsg.Contains("CharacterGoals.CharacterId", StringComparison.OrdinalIgnoreCase) ||
                         innerMsg.Contains("CharacterGoals.Title", StringComparison.OrdinalIgnoreCase) ||
                         innerMsg.Contains("IX_CharacterGoals_CharacterId_Title", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }

            inner = inner.InnerException;
        }

        var fullMsg = (ex.InnerException?.Message ?? "") + " " + (ex.Message ?? "");
        if ((fullMsg.Contains("23505") || fullMsg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)) &&
            (fullMsg.Contains("IX_CharacterGoals_CharacterId_Title", StringComparison.OrdinalIgnoreCase) ||
             (fullMsg.Contains("CharacterGoals", StringComparison.OrdinalIgnoreCase) && fullMsg.Contains("Title", StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        return false;
    }

    public static bool IsGoalProgressUniqueViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        while (inner != null)
        {
            if (inner is Npgsql.PostgresException pg)
            {
                if (pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation || pg.SqlState == "23505")
                {
                    if (string.IsNullOrWhiteSpace(pg.ConstraintName) ||
                        pg.ConstraintName.Contains("CharacterGoalExecutionProgresses", StringComparison.OrdinalIgnoreCase) ||
                        pg.ConstraintName.Contains("GoalId_ExecutionId", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            var sqlStateProp = inner.GetType().GetProperty("SqlState");
            if (sqlStateProp != null)
            {
                var sqlState = sqlStateProp.GetValue(inner)?.ToString();
                if (sqlState == "23505")
                {
                    var msg = inner.Message ?? "";
                    if (msg.Contains("CharacterGoalExecutionProgresses", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("GoalId_ExecutionId", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            var sqliteErrProp = inner.GetType().GetProperty("SqliteErrorCode");
            if (sqliteErrProp != null)
            {
                var errCode = sqliteErrProp.GetValue(inner);
                if (errCode is int code && code == 19)
                {
                    var innerMsg = inner.Message ?? "";
                    if (innerMsg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) &&
                        (innerMsg.Contains("CharacterGoalExecutionProgresses.GoalId", StringComparison.OrdinalIgnoreCase) ||
                         innerMsg.Contains("CharacterGoalExecutionProgresses.ExecutionId", StringComparison.OrdinalIgnoreCase) ||
                         innerMsg.Contains("CharacterGoalExecutionProgresses", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }

            inner = inner.InnerException;
        }

        var fullMsg = (ex.InnerException?.Message ?? "") + " " + (ex.Message ?? "");
        if ((fullMsg.Contains("23505") || fullMsg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)) &&
            (fullMsg.Contains("CharacterGoalExecutionProgresses", StringComparison.OrdinalIgnoreCase) ||
             fullMsg.Contains("GoalId_ExecutionId", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}
