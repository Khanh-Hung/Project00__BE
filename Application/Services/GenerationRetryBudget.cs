using Domain.Enums;

namespace Application.Services;

/// <summary>
/// Authoritative cost and time-bounded retry budget manager.
/// Enforces hard limits: Maximum 3 attempts, bounded total generation duration,
/// and prevents infinite/recursive retries while respecting PR24 quality thresholds.
/// </summary>
public sealed class GenerationRetryBudget
{
    public int MaxAttempts { get; }
    public TimeSpan MaxTotalGenerationTime { get; }

    public static GenerationRetryBudget Default => new(
        maxAttempts: 3,
        maxTotalGenerationTime: TimeSpan.FromSeconds(90)
    );

    public GenerationRetryBudget(
        int maxAttempts = 3,
        TimeSpan? maxTotalGenerationTime = null)
    {
        if (maxAttempts <= 0 || maxAttempts > 10)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "MaxAttempts must be between 1 and 10.");

        var resolvedMaxTime = maxTotalGenerationTime ?? TimeSpan.FromSeconds(90);
        if (resolvedMaxTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxTotalGenerationTime), "MaxTotalGenerationTime must be greater than zero.");

        MaxAttempts = maxAttempts;
        MaxTotalGenerationTime = resolvedMaxTime;
    }

    /// <summary>
    /// Evaluates if an operational failure from a completed attempt can schedule a subsequent retry attempt.
    /// </summary>
    public bool CanRetryFailure(
        int currentAttemptNumber,
        TimeSpan elapsedTotalTime,
        GenerationFailureCategory category,
        out string? budgetExhaustionReason)
    {
        if (!GenerationRetryPolicy.IsRetryable(category))
        {
            budgetExhaustionReason = $"Failure category {category} is non-retryable";
            return false;
        }

        if (currentAttemptNumber >= MaxAttempts)
        {
            budgetExhaustionReason = $"Maximum attempt budget of {MaxAttempts} attempts exhausted";
            return false;
        }

        if (elapsedTotalTime >= MaxTotalGenerationTime)
        {
            budgetExhaustionReason = $"Maximum generation duration budget of {MaxTotalGenerationTime.TotalSeconds:F1}s exceeded ({elapsedTotalTime.TotalSeconds:F1}s elapsed)";
            return false;
        }

        budgetExhaustionReason = null;
        return true;
    }

    /// <summary>
    /// Evaluates if an upcoming mitigation attempt number (e.g. attempt 2, 3) is within the attempt and duration budget.
    /// </summary>
    public bool CanRetryMitigation(
        int targetAttemptNumber,
        TimeSpan elapsedTotalTime,
        out string? budgetExhaustionReason)
    {
        if (targetAttemptNumber > MaxAttempts)
        {
            budgetExhaustionReason = $"Target attempt {targetAttemptNumber} exceeds maximum attempt budget of {MaxAttempts}";
            return false;
        }

        if (elapsedTotalTime >= MaxTotalGenerationTime)
        {
            budgetExhaustionReason = $"Maximum generation duration budget of {MaxTotalGenerationTime.TotalSeconds:F1}s exceeded ({elapsedTotalTime.TotalSeconds:F1}s elapsed)";
            return false;
        }

        budgetExhaustionReason = null;
        return true;
    }
}
