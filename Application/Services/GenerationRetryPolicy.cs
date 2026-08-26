using Domain.Enums;

namespace Application.Services;

/// <summary>
/// Authoritative retry and exponential backoff policy for generation failures.
/// Enforces bounded retries, jittered delays, and failure classification rules.
/// </summary>
public sealed class GenerationRetryPolicy
{
    public int MaxRetries { get; }
    public TimeSpan BaseDelay { get; }
    public TimeSpan MaxDelay { get; }
    public double JitterRatio { get; }
    public bool DeterministicMode { get; }

    public static GenerationRetryPolicy Default => new(
        maxRetries: 3,
        baseDelay: TimeSpan.FromSeconds(1),
        maxDelay: TimeSpan.FromSeconds(30),
        jitterRatio: 0.2,
        deterministicMode: false
    );

    public static GenerationRetryPolicy Deterministic(int maxRetries = 3, TimeSpan? baseDelay = null) => new(
        maxRetries: maxRetries,
        baseDelay: baseDelay ?? TimeSpan.FromSeconds(1),
        maxDelay: TimeSpan.FromSeconds(30),
        jitterRatio: 0.0,
        deterministicMode: true
    );

    public GenerationRetryPolicy(
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        double jitterRatio = 0.2,
        bool deterministicMode = false)
    {
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "MaxRetries cannot be negative.");
        if (jitterRatio < 0.0 || jitterRatio > 1.0)
            throw new ArgumentOutOfRangeException(nameof(jitterRatio), "JitterRatio must be between 0.0 and 1.0.");

        MaxRetries = maxRetries;
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
        JitterRatio = jitterRatio;
        DeterministicMode = deterministicMode;
    }

    /// <summary>
    /// Determines whether a given failure category is eligible for retry.
    /// </summary>
    public static bool IsRetryable(GenerationFailureCategory category) => category switch
    {
        GenerationFailureCategory.ProviderTimeout => true,
        GenerationFailureCategory.ProviderUnavailable => true,
        GenerationFailureCategory.ProviderRateLimited => true,
        GenerationFailureCategory.TransientNetwork => true,
        GenerationFailureCategory.GpuFailure => true,
        GenerationFailureCategory.DatabaseTransient => true,
        _ => false
    };

    /// <summary>
    /// Evaluates if an attempt should be retried and computes the backoff delay.
    /// </summary>
    public bool ShouldRetry(GenerationFailureCategory category, int currentRetryCount, out TimeSpan delay, long? deterministicSeed = null)
    {
        if (!IsRetryable(category) || currentRetryCount >= MaxRetries)
        {
            delay = TimeSpan.Zero;
            return false;
        }

        delay = CalculateDelay(currentRetryCount, deterministicSeed);
        return true;
    }

    /// <summary>
    /// Computes exponential backoff with jitter: delay = min(MaxDelay, BaseDelay * 2^retryCount) +/- jitter.
    /// Semantics:
    /// 1. DeterministicMode == true: Returns exact nominal delay without jitter.
    /// 2. DeterministicMode == false with deterministicSeed: Calculates reproducible jitter based on seed.
    /// 3. DeterministicMode == false with no seed: Calculates pseudo-random jitter.
    /// </summary>
    public TimeSpan CalculateDelay(int retryCount, long? deterministicSeed = null)
    {
        double multiplier = Math.Pow(2.0, Math.Min(retryCount, 10));
        double rawSeconds = BaseDelay.TotalSeconds * multiplier;
        double cappedSeconds = Math.Min(rawSeconds, MaxDelay.TotalSeconds);

        if (DeterministicMode || JitterRatio <= 0.0)
        {
            return TimeSpan.FromSeconds(cappedSeconds);
        }

        double jitterFactor;
        if (deterministicSeed.HasValue)
        {
            var random = new Random((int)(deterministicSeed.Value ^ retryCount));
            jitterFactor = (random.NextDouble() * 2.0 - 1.0) * JitterRatio;
        }
        else
        {
            jitterFactor = (Random.Shared.NextDouble() * 2.0 - 1.0) * JitterRatio;
        }

        double jitteredSeconds = Math.Max(0.1, cappedSeconds * (1.0 + jitterFactor));
        return TimeSpan.FromSeconds(jitteredSeconds);
    }
}
