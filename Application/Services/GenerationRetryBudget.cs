using Domain.Enums;

namespace Application.Services;

/// <summary>
/// Authoritative cost and time-bounded retry budget manager.
/// Enforces hard limits: Maximum 3 attempts, bounded total generation duration,
/// and enforces hard execution deadlines across generation attempts using linked cancellation tokens.
/// </summary>
public sealed class GenerationRetryBudget
{
    public const int MaxAllowedAttempts = 3;
    public static readonly TimeSpan MinAttemptRemainingTime = TimeSpan.FromMilliseconds(500);

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
        if (maxAttempts <= 0 || maxAttempts > MaxAllowedAttempts)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), $"MaxAttempts must be between 1 and {MaxAllowedAttempts}.");

        var resolvedMaxTime = maxTotalGenerationTime ?? TimeSpan.FromSeconds(90);
        if (resolvedMaxTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxTotalGenerationTime), "MaxTotalGenerationTime must be greater than zero.");

        MaxAttempts = maxAttempts;
        MaxTotalGenerationTime = resolvedMaxTime;
    }

    /// <summary>
    /// Computes the exact remaining duration before the total generation deadline is exceeded.
    /// </summary>
    public TimeSpan GetRemainingTime(TimeSpan elapsedTotalTime)
    {
        if (elapsedTotalTime >= MaxTotalGenerationTime)
            return TimeSpan.Zero;

        return MaxTotalGenerationTime - elapsedTotalTime;
    }

    /// <summary>
    /// Creates a linked CancellationTokenSource configured with a hard deadline based on remaining total budget.
    /// </summary>
    public CancellationTokenSource CreateAttemptCancellationTokenSource(CancellationToken parentToken, TimeSpan elapsedTotalTime)
    {
        var remaining = GetRemainingTime(elapsedTotalTime);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        if (remaining > TimeSpan.Zero)
        {
            cts.CancelAfter(remaining);
        }
        else
        {
            cts.Cancel();
        }
        return cts;
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

        var remaining = GetRemainingTime(elapsedTotalTime);
        if (remaining < MinAttemptRemainingTime)
        {
            budgetExhaustionReason = $"Insufficient remaining generation duration ({remaining.TotalMilliseconds:F0}ms remaining, min required {MinAttemptRemainingTime.TotalMilliseconds:F0}ms)";
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

        var remaining = GetRemainingTime(elapsedTotalTime);
        if (remaining < MinAttemptRemainingTime)
        {
            budgetExhaustionReason = $"Insufficient remaining generation duration ({remaining.TotalMilliseconds:F0}ms remaining, min required {MinAttemptRemainingTime.TotalMilliseconds:F0}ms)";
            return false;
        }

        budgetExhaustionReason = null;
        return true;
    }
}
