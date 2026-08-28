using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Goals;

public sealed class GoalProgressService : IGoalProgressService
{
    private readonly CoreDbContext _dbContext;
    private readonly ILogger<GoalProgressService> _logger;

    public GoalProgressService(
        CoreDbContext dbContext,
        ILogger<GoalProgressService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GoalProgressResult> RecordContributionAsync(
        Guid goalId,
        Guid activityId,
        double contributionValue,
        DateTime? now = null,
        CancellationToken ct = default)
    {
        if (goalId == Guid.Empty)
            throw new ArgumentException("GoalId cannot be empty.", nameof(goalId));

        if (activityId == Guid.Empty)
            throw new ArgumentException("ActivityId cannot be empty.", nameof(activityId));

        if (contributionValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(contributionValue), "ContributionValue must be greater than zero.");

        var time = now ?? DateTime.UtcNow;

        // 1. Check idempotency first: If contribution already exists in DB, return immediately without touching Goal!
        bool alreadyRecorded = await _dbContext.GoalActivityContributions
            .AsNoTracking()
            .AnyAsync(c => c.GoalId == goalId && c.ActivityId == activityId, ct);

        if (alreadyRecorded)
        {
            _logger.LogInformation("[GoalProgressService] Contribution already exists for GoalId={GoalId}, ActivityId={ActivityId}. Returning idempotent result without mutating goal.",
                goalId, activityId);

            var existingGoal = await _dbContext.CharacterGoals
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == goalId, ct);

            return new GoalProgressResult(
                Success: true,
                IsDuplicateContribution: true,
                ContributionValue: contributionValue,
                PreviousProgress: existingGoal?.Progress ?? 0f,
                NewProgress: existingGoal?.Progress ?? 0f,
                MilestoneCompleted: false,
                GoalCompleted: existingGoal?.Status == CharacterGoalStatus.Completed,
                Message: "Idempotent duplicate contribution suppressed."
            );
        }

        // 2. Fetch Goal Aggregate
        var goal = await _dbContext.CharacterGoals
            .Include(g => g.Milestones)
            .FirstOrDefaultAsync(g => g.Id == goalId, ct);

        if (goal == null)
        {
            return new GoalProgressResult(
                Success: false,
                IsDuplicateContribution: false,
                ContributionValue: contributionValue,
                PreviousProgress: 0f,
                NewProgress: 0f,
                MilestoneCompleted: false,
                GoalCompleted: false,
                Message: $"Goal {goalId} not found."
            );
        }

        if (goal.Status != CharacterGoalStatus.Active)
        {
            return new GoalProgressResult(
                Success: false,
                IsDuplicateContribution: false,
                ContributionValue: contributionValue,
                PreviousProgress: goal.Progress,
                NewProgress: goal.Progress,
                MilestoneCompleted: false,
                GoalCompleted: false,
                Message: $"Cannot record contribution for goal with status {goal.Status}."
            );
        }

        float prevProgress = goal.Progress;
        var completedMilestonesBefore = goal.Milestones
            .Where(m => m.Status == CharacterGoalMilestoneStatus.Completed)
            .Select(m => m.Id)
            .ToHashSet();

        // 3. Prepare Contribution Entity
        var contribution = new GoalActivityContribution(
            goalId: goalId,
            activityId: activityId,
            contributionValue: contributionValue,
            createdAt: time
        );

        // 4. Mutate Goal Progress on Domain Aggregate
        goal.RecordProgress(contributionValue, time);

        // 5. Atomic Persistence with Duplicate Race Condition Protection
        try
        {
            await _dbContext.GoalActivityContributions.AddAsync(contribution, ct);
            await _dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            if (IsDuplicateContributionViolation(ex))
            {
                _logger.LogInformation(ex, "[GoalProgressService] Concurrent duplicate goal contribution suppressed for GoalId={GoalId}, ActivityId={ActivityId}",
                    goalId, activityId);

                // Detach entities from DbContext to prevent dirty tracking
                _dbContext.Entry(contribution).State = EntityState.Detached;
                _dbContext.Entry(goal).State = EntityState.Detached;

                return new GoalProgressResult(
                    Success: true,
                    IsDuplicateContribution: true,
                    ContributionValue: contributionValue,
                    PreviousProgress: prevProgress,
                    NewProgress: prevProgress,
                    MilestoneCompleted: false,
                    GoalCompleted: false,
                    Message: "Idempotent duplicate contribution suppressed."
                );
            }

            _logger.LogError(ex, "[GoalProgressService] Fatal DB error recording contribution for GoalId={GoalId}, ActivityId={ActivityId}",
                goalId, activityId);
            throw;
        }

        bool milestoneCompleted = goal.Milestones.Any(m =>
            m.Status == CharacterGoalMilestoneStatus.Completed && !completedMilestonesBefore.Contains(m.Id));
        bool goalCompleted = goal.Status == CharacterGoalStatus.Completed;

        _logger.LogInformation(
            "[GoalProgressService] Successfully recorded contribution for GoalId={GoalId}, ActivityId={ActivityId}. Progress: {Prev:P1} -> {New:P1}, MilestoneCompleted={MComp}, GoalCompleted={GComp}",
            goalId, activityId, prevProgress, goal.Progress, milestoneCompleted, goalCompleted);

        return new GoalProgressResult(
            Success: true,
            IsDuplicateContribution: false,
            ContributionValue: contributionValue,
            PreviousProgress: prevProgress,
            NewProgress: goal.Progress,
            MilestoneCompleted: milestoneCompleted,
            GoalCompleted: goalCompleted,
            Message: "Contribution recorded successfully."
        );
    }

    public static bool IsDuplicateContributionViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        while (inner != null)
        {
            if (inner is Npgsql.PostgresException pg)
            {
                if (pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
                {
                    if (string.IsNullOrWhiteSpace(pg.ConstraintName) ||
                        pg.ConstraintName.Contains("GoalActivityContributions", StringComparison.OrdinalIgnoreCase) ||
                        pg.ConstraintName.Contains("GoalId_ActivityId", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            var sqlStateProp = inner.GetType().GetProperty("SqlState");
            if (sqlStateProp != null)
            {
                var sqlState = sqlStateProp.GetValue(inner)?.ToString();
                if (sqlState == Npgsql.PostgresErrorCodes.UniqueViolation || sqlState == "23505")
                {
                    return true;
                }
                return false;
            }

            var sqliteErrProp = inner.GetType().GetProperty("SqliteErrorCode");
            if (sqliteErrProp != null)
            {
                var errCode = sqliteErrProp.GetValue(inner);
                if (errCode is int code && code == 19) 
                {
                    var innerMsg = inner.Message ?? "";
                    if (innerMsg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) &&
                        innerMsg.Contains("GoalActivityContributions", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            inner = inner.InnerException;
        }

        var msg = (ex.InnerException?.Message ?? "") + " " + (ex.Message ?? "");
        if (msg.Contains("IX_GoalActivityContributions_GoalId_ActivityId", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("UNIQUE constraint failed: GoalActivityContributions.GoalId, GoalActivityContributions.ActivityId", StringComparison.OrdinalIgnoreCase) ||
            (msg.Contains("23505", StringComparison.OrdinalIgnoreCase) && msg.Contains("GoalActivityContributions", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}
