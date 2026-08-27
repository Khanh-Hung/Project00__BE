using Application.Telemetry;
using Domain.Enums;

namespace Application.Interfaces;

/// <summary>
/// Domain-aligned metrics interface decoupled from concrete monitoring infrastructure (Prometheus, OpenTelemetry, etc.).
/// </summary>
public interface IGenerationMetrics
{
    void RecordGenerationStarted(Guid jobId, Guid requestId);

    void RecordGenerationCompleted(Guid jobId, int attempts, GenerationTiming timing);

    void RecordGenerationFailed(Guid jobId, GenerationFailureCategory category, int attempts, TimeSpan totalDuration);

    void RecordGenerationRetry(Guid jobId, int attemptNumber, TimeSpan retryDelay);

    void RecordGenerationQuarantined(Guid jobId, int attempts, float? finalSimilarity, float? finalFeatureScore);

    void RecordIdentityEvaluation(
        Guid jobId,
        Guid attemptId,
        int attemptNumber,
        float identitySimilarity,
        float featureScore,
        bool passed,
        TimeSpan duration);

    void RecordTiming(GenerationTiming timing);
}
