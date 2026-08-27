using Domain.Enums;

namespace Application.Services;

/// <summary>
/// Authoritative policy defining retry parameters, backoff intervals, jitter calculations,
/// and retry eligibility per generation failure category.
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

    public static GenerationRetryPolicy Deterministic(
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null) => new(
        maxRetries: maxRetries,
        baseDelay: baseDelay ?? TimeSpan.FromSeconds(1),
        maxDelay: maxDelay ?? TimeSpan.FromSeconds(30),
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

        var resolvedBaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        var resolvedMaxDelay = maxDelay ?? TimeSpan.FromSeconds(30);

        if (resolvedBaseDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "BaseDelay must be greater than zero.");
        if (resolvedMaxDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "MaxDelay must be greater than zero.");
        if (resolvedMaxDelay < resolvedBaseDelay)
            throw new ArgumentException("MaxDelay must be greater than or equal to BaseDelay.", nameof(maxDelay));

        MaxRetries = maxRetries;
        BaseDelay = resolvedBaseDelay;
        MaxDelay = resolvedMaxDelay;
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
    /// 2. DeterministicMode == false with deterministicSeed: Calculates reproducible jitter based on 64-bit SplitMix64 PRNG.
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
            ulong combinedSeed = (ulong)deterministicSeed.Value ^ ((ulong)retryCount << 32 | (uint)retryCount);
            double sample = SplitMix64ToDouble(combinedSeed);
            jitterFactor = (sample * 2.0 - 1.0) * JitterRatio;
        }
        else
        {
            jitterFactor = (Random.Shared.NextDouble() * 2.0 - 1.0) * JitterRatio;
        }

        double jitteredSeconds = Math.Max(0.1, cappedSeconds * (1.0 + jitterFactor));
        return TimeSpan.FromSeconds(jitteredSeconds);
    }

    /// <summary>
    /// High-entropy 64-bit SplitMix64 pseudo-random generator mapping 64-bit seed state to uniform double in [0, 1).
    /// </summary>
    private static double SplitMix64ToDouble(ulong x)
    {
        x += 0x9e3779b97f4a7c15;
        x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9;
        x = (x ^ (x >> 27)) * 0x94d049bb133111eb;
        x = x ^ (x >> 31);
        return (x >> 11) * (1.0 / (1UL << 53));
    }
}
