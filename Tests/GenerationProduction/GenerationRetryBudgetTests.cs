using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
    public void GenerationRetryPolicy_CalculateDelay_ComputesExpectedDelays()
    {
        var deterministicPolicy = GenerationRetryPolicy.Deterministic(
            maxRetries: 3,
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(30)
        );

        // 2^0 * 1s = 1s
        Assert.Equal(TimeSpan.FromSeconds(1), deterministicPolicy.CalculateDelay(0));
        // 2^1 * 1s = 2s
        Assert.Equal(TimeSpan.FromSeconds(2), deterministicPolicy.CalculateDelay(1));
        // 2^2 * 1s = 4s
        Assert.Equal(TimeSpan.FromSeconds(4), deterministicPolicy.CalculateDelay(2));
        // 2^3 * 1s = 8s
        Assert.Equal(TimeSpan.FromSeconds(8), deterministicPolicy.CalculateDelay(3));
    }

    [Fact]
    public async Task CanRetryFailure_False_WhenMaxAttemptsExhausted_ResultsInExactSingleTerminalJobFailure_AndCannotBeRetriedByWorker()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new CoreDbContext(options);
        var clock = new SystemDateTimeProvider();

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, characterId, sceneRevision: 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(5), clock.UtcNow);
        await dbContext.ImageGenerationJobs.AddAsync(job);

        var attempt = new ImageGenerationAttempt(
            generationJobId: job.Id,
            turnId: job.TurnId,
            sceneRevision: 1,
            attemptNumber: 3, // Already at attempt 3 (exhausted)
            derivedSeed: 12345L,
            parametersJson: "{}",
            generationFingerprint: "test-fp",
            status: GenerationAttemptStatus.Running,
            claimedBy: "worker-1",
            startedAt: clock.UtcNow,
            leaseUntil: clock.UtcNow.AddMinutes(2)
        );
        await dbContext.ImageGenerationAttempts.AddAsync(attempt);
        await dbContext.SaveChangesAsync();

        var budget = new GenerationRetryBudget(maxAttempts: 3);
        var canRetry = budget.CanRetryFailure(
            currentAttemptNumber: 3,
            elapsedTotalTime: TimeSpan.FromSeconds(20),
            category: GenerationFailureCategory.GpuFailure,
            out var failReason
        );

        Assert.False(canRetry);
        Assert.Contains("exhausted", failReason, StringComparison.OrdinalIgnoreCase);

        // Transition job to failed terminal
        job.Fail("GPU crashed on attempt 3", isRetryable: false, now: clock.UtcNow, workerId: "worker-1");
        await dbContext.SaveChangesAsync();

        var reloadedJob = await dbContext.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
        Assert.Equal(ImageJobStatus.Failed, reloadedJob.Status);
        Assert.False(reloadedJob.IsRetryable);
        Assert.NotNull(reloadedJob.CompletedAt);

        // Verify that another worker CANNOT claim or retry this terminal job
        var secondClaim = reloadedJob.TryClaim("worker-2", TimeSpan.FromMinutes(5), clock.UtcNow);
        Assert.False(secondClaim);
    }

    [Fact]
    public void RetryBudget_Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerationRetryBudget(maxAttempts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerationRetryBudget(maxAttempts: 4)); // Invariant: <= 3
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerationRetryBudget(maxAttempts: 3, maxTotalGenerationTime: TimeSpan.Zero));
    }
}
