using Application.Services;
using Domain.Enums;
using Xunit;

namespace Tests.GenerationReliability;

public sealed class GenerationRetryPolicyTests
{
    [Theory]
    [InlineData(GenerationFailureCategory.ProviderTimeout, true)]
    [InlineData(GenerationFailureCategory.ProviderUnavailable, true)]
    [InlineData(GenerationFailureCategory.ProviderRateLimited, true)]
    [InlineData(GenerationFailureCategory.TransientNetwork, true)]
    [InlineData(GenerationFailureCategory.GpuFailure, true)]
    [InlineData(GenerationFailureCategory.DatabaseTransient, true)]
    [InlineData(GenerationFailureCategory.InvalidWorkflow, false)]
    [InlineData(GenerationFailureCategory.InvalidInput, false)]
    [InlineData(GenerationFailureCategory.ConfigurationError, false)]
    [InlineData(GenerationFailureCategory.Cancellation, false)]
    [InlineData(GenerationFailureCategory.LeaseLost, false)]
    [InlineData(GenerationFailureCategory.Unknown, false)]
    public void IsRetryable_ClassifiesCorrectly(GenerationFailureCategory category, bool expectedRetryable)
    {
        var result = GenerationRetryPolicy.IsRetryable(category);
        Assert.Equal(expectedRetryable, result);
    }

    [Fact]
    public void ShouldRetry_WhenMaxRetriesExceeded_ReturnsFalse()
    {
        var policy = new GenerationRetryPolicy(maxRetries: 3);

        var retry0 = policy.ShouldRetry(GenerationFailureCategory.ProviderTimeout, currentRetryCount: 0, out var d0);
        var retry1 = policy.ShouldRetry(GenerationFailureCategory.ProviderTimeout, currentRetryCount: 1, out var d1);
        var retry2 = policy.ShouldRetry(GenerationFailureCategory.ProviderTimeout, currentRetryCount: 2, out var d2);
        var retry3 = policy.ShouldRetry(GenerationFailureCategory.ProviderTimeout, currentRetryCount: 3, out var d3);

        Assert.True(retry0);
        Assert.True(retry1);
        Assert.True(retry2);
        Assert.False(retry3);
        Assert.Equal(TimeSpan.Zero, d3);
    }

    [Fact]
    public void DeterministicBackoff_CalculatesExponentialDelaysCorrectly()
    {
        var policy = GenerationRetryPolicy.Deterministic(maxRetries: 5, baseDelay: TimeSpan.FromSeconds(1));

        var delay0 = policy.CalculateDelay(0);
        var delay1 = policy.CalculateDelay(1);
        var delay2 = policy.CalculateDelay(2);
        var delay3 = policy.CalculateDelay(3);

        Assert.Equal(TimeSpan.FromSeconds(1), delay0); // 1 * 2^0 = 1s
        Assert.Equal(TimeSpan.FromSeconds(2), delay1); // 1 * 2^1 = 2s
        Assert.Equal(TimeSpan.FromSeconds(4), delay2); // 1 * 2^2 = 4s
        Assert.Equal(TimeSpan.FromSeconds(8), delay3); // 1 * 2^3 = 8s
    }

    [Fact]
    public void Backoff_ClampsToMaxDelay()
    {
        var policy = new GenerationRetryPolicy(
            maxRetries: 10,
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(10),
            jitterRatio: 0.0,
            deterministicMode: true);

        var delay = policy.CalculateDelay(5); // 1 * 2^5 = 32s -> clamped to 10s
        Assert.Equal(TimeSpan.FromSeconds(10), delay);
    }

    [Fact]
    public void Jitter_StaysWithinConfiguredBounds()
    {
        var policy = new GenerationRetryPolicy(
            maxRetries: 3,
            baseDelay: TimeSpan.FromSeconds(10),
            maxDelay: TimeSpan.FromSeconds(30),
            jitterRatio: 0.2,
            deterministicMode: false);

        for (int i = 0; i < 20; i++)
        {
            var delay = policy.CalculateDelay(0); // nominal 10s, bounds [8s, 12s]
            Assert.InRange(delay.TotalSeconds, 8.0, 12.0);
        }
    }
}
