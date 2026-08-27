using Application.Services;
using Domain.Enums;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class GenerationRetryBudgetTests
{
    [Fact]
    public void RetryBudget_AllowsRetryWithinBudget()
    {
        var budget = new GenerationRetryBudget(maxAttempts: 3, maxTotalGenerationTime: TimeSpan.FromSeconds(90));

        var allowed = budget.CanRetryFailure(
            currentAttemptNumber: 1,
            elapsedTotalTime: TimeSpan.FromSeconds(15),
            category: GenerationFailureCategory.ProviderTimeout,
            out var reason
        );

        Assert.True(allowed);
        Assert.Null(reason);
    }

    [Fact]
    public void RetryBudget_RejectsNonRetryableFailureCategory()
    {
        var budget = GenerationRetryBudget.Default;

        var allowed = budget.CanRetryFailure(
            currentAttemptNumber: 1,
            elapsedTotalTime: TimeSpan.FromSeconds(10),
            category: GenerationFailureCategory.InvalidWorkflow,
            out var reason
        );

        Assert.False(allowed);
        Assert.Contains("non-retryable", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryBudget_RejectsWhenMaxAttemptsExhausted()
    {
        var budget = new GenerationRetryBudget(maxAttempts: 3);

        var allowed = budget.CanRetryFailure(
            currentAttemptNumber: 3,
            elapsedTotalTime: TimeSpan.FromSeconds(20),
            category: GenerationFailureCategory.GpuFailure,
            out var reason
        );

        Assert.False(allowed);
        Assert.Contains("exhausted", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryBudget_RejectsWhenMaxTotalTimeExhausted()
    {
        var budget = new GenerationRetryBudget(maxAttempts: 3, maxTotalGenerationTime: TimeSpan.FromSeconds(30));

        var allowed = budget.CanRetryFailure(
            currentAttemptNumber: 2,
            elapsedTotalTime: TimeSpan.FromSeconds(35),
            category: GenerationFailureCategory.ProviderTimeout,
            out var reason
        );

        Assert.False(allowed);
        Assert.Contains("Insufficient remaining", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryBudget_DoesNotStartAttemptWhenRemainingBudgetIsInsufficient()
    {
        // 90s total budget, elapsed 89.6s -> remaining 0.4s (< min 0.5s buffer)
        var budget = new GenerationRetryBudget(maxAttempts: 3, maxTotalGenerationTime: TimeSpan.FromSeconds(90));

        var allowed = budget.CanRetryMitigation(
            targetAttemptNumber: 2,
            elapsedTotalTime: TimeSpan.FromSeconds(89.6),
            out var reason
        );

        Assert.False(allowed);
        Assert.Contains("Insufficient remaining generation duration", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryBudget_TotalExecutionNeverExceedsConfiguredDeadline()
    {
        var budget = new GenerationRetryBudget(maxAttempts: 3, maxTotalGenerationTime: TimeSpan.FromSeconds(90));

        // When elapsed is 80s, remaining time is exactly 10s
        var remaining = budget.GetRemainingTime(TimeSpan.FromSeconds(80));
        Assert.Equal(TimeSpan.FromSeconds(10), remaining);

        // Linked token will be scheduled to cancel after remaining duration
        using var cts = budget.CreateAttemptCancellationTokenSource(CancellationToken.None, TimeSpan.FromSeconds(80));
        Assert.False(cts.IsCancellationRequested);

        // When elapsed reaches or exceeds deadline, token is canceled immediately
        using var expiredCts = budget.CreateAttemptCancellationTokenSource(CancellationToken.None, TimeSpan.FromSeconds(90.5));
        Assert.True(expiredCts.IsCancellationRequested);
    }

    [Fact]
    public void RetryBudget_CanRetryMitigation_RespectsMaxAttemptsAndTime()
    {
        var budget = new GenerationRetryBudget(maxAttempts: 3, maxTotalGenerationTime: TimeSpan.FromSeconds(60));

        Assert.True(budget.CanRetryMitigation(1, TimeSpan.FromSeconds(10), out _));
        Assert.True(budget.CanRetryMitigation(2, TimeSpan.FromSeconds(20), out _));
        Assert.True(budget.CanRetryMitigation(3, TimeSpan.FromSeconds(30), out _));
        Assert.False(budget.CanRetryMitigation(4, TimeSpan.FromSeconds(40), out var reasonAttempt));
        Assert.Contains("exceeds", reasonAttempt, StringComparison.OrdinalIgnoreCase);

        Assert.False(budget.CanRetryMitigation(2, TimeSpan.FromSeconds(59.8), out var reasonTime));
        Assert.Contains("Insufficient remaining", reasonTime, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryBudget_Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerationRetryBudget(maxAttempts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerationRetryBudget(maxAttempts: 4)); // Invariant: <= 3
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerationRetryBudget(maxAttempts: 3, maxTotalGenerationTime: TimeSpan.Zero));
    }
}
