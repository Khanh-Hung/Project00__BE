using Domain.Enums;

namespace Application.Services;

/// <summary>
/// Authoritative cost and time-bounded retry budget manager.
/// Architectural Separation of Concerns:
/// - GenerationRetryBudget determines IF an attempt or retry is permissible based on hard resource limits
///   (max 3 attempts, 90s total wall-clock execution deadline, minimum attempt execution buffer).
/// - GenerationRetryPolicy determines WHEN an operational retry should execute by calculating exponential
///   backoff delay intervals with jitter.
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
    /// Creates a linked CancellationTokenSource configured with an exact timeout corresponding to the
    /// remaining duration in the generation retry budget.
    /// If the remaining budget is zero or already expired, the token will be in a canceled state.
    /// </summary>
    public CancellationTokenSource CreateAttemptCancellationTokenSource(CancellationToken parentToken, TimeSpan elapsedTotalTime)
    {
        var remaining = GetRemainingTime(elapsedTotalTime);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);

        if (remaining <= TimeSpan.Zero)
        {
            cts.Cancel();
        }
        else
        {
            cts.CancelAfter(remaining);
        }

        return cts;
    }

    /// <summary>
    /// Evaluates if a quality guard mitigation attempt can proceed under the budget.
    /// Invariant: Target attempt number must be &lt;= MaxAttempts and remaining generation time must exceed MinAttemptRemainingTime.
    /// </summary>
    public bool CanRetryMitigation(
        int targetAttemptNumber,
        TimeSpan elapsedTotalTime,
        out string? reason)
    {
        if (targetAttemptNumber > MaxAttempts)
        {
            reason = $"Target attempt {targetAttemptNumber} exceeds maximum allowed attempts ({MaxAttempts}).";
            return false;
        }

        var remainingTime = GetRemainingTime(elapsedTotalTime);
        if (remainingTime < MinAttemptRemainingTime)
        {
            reason = $"Insufficient remaining generation duration ({remainingTime.TotalMilliseconds:F0}ms &lt; {MinAttemptRemainingTime.TotalMilliseconds:F0}ms). Total budget: {MaxTotalGenerationTime.TotalSeconds:F0}s, Elapsed: {elapsedTotalTime.TotalSeconds:F1}s.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Evaluates if an operational failure attempt can proceed under the budget.
    /// Invariant: Failure category must be retryable, current attempt &lt; MaxAttempts, and remaining time &gt;= MinAttemptRemainingTime.
    /// </summary>
    public bool CanRetryFailure(
        int currentAttemptNumber,
        TimeSpan elapsedTotalTime,
        GenerationFailureCategory category,
        out string? reason)
    {
        if (!GenerationRetryPolicy.IsRetryable(category))
        {
            reason = $"Failure category '{category}' is non-retryable.";
            return false;
        }

        if (currentAttemptNumber >= MaxAttempts)
        {
            reason = $"Current attempt {currentAttemptNumber} exhausted maximum allowed retry budget ({MaxAttempts}).";
            return false;
        }

        var remainingTime = GetRemainingTime(elapsedTotalTime);
        if (remainingTime < MinAttemptRemainingTime)
        {
            reason = $"Insufficient remaining generation duration ({remainingTime.TotalMilliseconds:F0}ms &lt; {MinAttemptRemainingTime.TotalMilliseconds:F0}ms). Total budget: {MaxTotalGenerationTime.TotalSeconds:F0}s, Elapsed: {elapsedTotalTime.TotalSeconds:F1}s.";
            return false;
        }

        reason = null;
        return true;
    }
}
