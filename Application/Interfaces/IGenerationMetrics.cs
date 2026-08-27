using Application.Telemetry;
using Domain.Enums;

namespace Application.Interfaces;

/// <summary>
/// Domain-aligned metrics interface decoupled from concrete monitoring infrastructure (Prometheus, OpenTelemetry, etc.).
/// </summary>
public interface IGenerationMetrics
{
    void RecordGenerationStarted(Guid jobId, Guid requestId);

    void RecordAttemptStarted(Guid jobId, int attemptNumber);

    void RecordGenerationCompleted(Guid jobId, int attempts, GenerationTiming timing);

    void RecordGenerationFailed(Guid jobId, GenerationFailureCategory category, int attempts, TimeSpan totalDuration);

    /// <summary>
    /// Records a retry event.
    /// </summary>
    /// <param name="jobId">Unique identifier of the generation job.</param>
    /// <param name="attemptNumber">Current attempt number initiating the retry.</param>
    /// <param name="retryDelay">Scheduled backoff delay for operational retries, or TimeSpan.Zero for immediate quality mitigation attempts.</param>
    void RecordGenerationRetry(Guid jobId, int attemptNumber, TimeSpan retryDelay);

    void RecordGenerationQuarantined(Guid jobId, int attempts, float? finalSimilarity, float? finalFeatureScore);

    void RecordIdentityEvaluation(
        Guid jobId,
        Guid attemptId,
        int attemptNumber,
        float identitySimilarity,
        float featureScore,
        bool passed,
        bool willRetry,
        TimeSpan duration);

    void RecordTiming(GenerationTiming timing);
}
