using Application.Common;
using Application.Interfaces;
using Application.Telemetry;
using Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Telemetry;

/// <summary>
/// Infrastructure telemetry implementation for generation metrics.
/// Emits OpenTelemetry metrics via GenerationObservability and records structured log events.
/// Strictly omits raw prompt sensitive payloads and raw image byte data from logs.
/// </summary>
public sealed class GenerationMetrics : IGenerationMetrics
{
    private readonly ILogger<GenerationMetrics> _logger;

    public GenerationMetrics(ILogger<GenerationMetrics> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RecordGenerationStarted(Guid jobId, Guid requestId)
    {
        GenerationObservability.RequestsTotal.Add(1);
        GenerationObservability.JobsTotal.Add(1);

        _logger.LogInformation("[GenerationStarted] JobId={JobId}, RequestId={RequestId}", jobId, requestId);
    }

    public void RecordGenerationCompleted(Guid jobId, int attempts, GenerationTiming timing)
    {
        GenerationObservability.JobsCompletedTotal.Add(1);
        GenerationObservability.AttemptsTotal.Add(attempts);

        RecordTiming(timing);

        _logger.LogInformation("[GenerationCompleted] JobId={JobId}, Attempts={Attempts}, TotalLatencyMs={TotalLatencyMs:F1}, GenLatencyMs={GenLatencyMs:F1}, EvalLatencyMs={EvalLatencyMs:F1}, AcceptLatencyMs={AcceptLatencyMs:F1}",
            jobId, attempts, timing.TotalLatency.TotalMilliseconds, timing.GenerationLatency.TotalMilliseconds, timing.EvaluationLatency.TotalMilliseconds, timing.AcceptanceLatency.TotalMilliseconds);
    }

    public void RecordGenerationFailed(Guid jobId, GenerationFailureCategory category, int attempts, TimeSpan totalDuration)
    {
        GenerationObservability.JobsFailedTotal.Add(1);
        GenerationObservability.AttemptsTotal.Add(attempts);
        GenerationObservability.TotalLatencyMs.Record(totalDuration.TotalMilliseconds);

        _logger.LogError("[GenerationFailed] JobId={JobId}, Category={Category}, Attempts={Attempts}, TotalDurationMs={TotalDurationMs:F1}",
            jobId, category, attempts, totalDuration.TotalMilliseconds);
    }

    public void RecordGenerationRetry(Guid jobId, int attemptNumber, TimeSpan retryDelay)
    {
        GenerationObservability.RetriesTotal.Add(1);

        _logger.LogWarning("[GenerationRetryScheduled] JobId={JobId}, AttemptNumber={AttemptNumber}, RetryDelayMs={RetryDelayMs:F1}",
            jobId, attemptNumber, retryDelay.TotalMilliseconds);
    }

    public void RecordGenerationQuarantined(Guid jobId, int attempts, float? finalSimilarity, float? finalFeatureScore)
    {
        GenerationObservability.JobsQuarantinedTotal.Add(1);
        GenerationObservability.IdentityGuardQuarantineTotal.Add(1);
        GenerationObservability.AttemptsTotal.Add(attempts);

        _logger.LogWarning("[GenerationQuarantined] JobId={JobId}, Attempts={Attempts}, FinalSimilarity={FinalSimilarity:F4}, FinalFeatureScore={FinalFeatureScore:F4}",
            jobId, attempts, finalSimilarity, finalFeatureScore);
    }

    public void RecordIdentityEvaluation(
        Guid jobId,
        Guid attemptId,
        int attemptNumber,
        float identitySimilarity,
        float featureScore,
        bool passed,
        TimeSpan duration)
    {
        GenerationObservability.IdentityGuardTriggerTotal.Add(1);
        GenerationObservability.EvaluationLatencyMs.Record(duration.TotalMilliseconds);

        if (passed && attemptNumber > 1)
        {
            GenerationObservability.IdentityGuardRecoveryTotal.Add(1);
        }

        _logger.LogInformation("[IdentityEvaluated] JobId={JobId}, AttemptId={AttemptId}, AttemptNumber={AttemptNumber}, Similarity={Similarity:F4}, FeatureScore={FeatureScore:F4}, Passed={Passed}, DurationMs={DurationMs:F1}",
            jobId, attemptId, attemptNumber, identitySimilarity, featureScore, passed, duration.TotalMilliseconds);
    }

    public void RecordTiming(GenerationTiming timing)
    {
        if (timing.QueueLatency > TimeSpan.Zero)
            GenerationObservability.QueueLatencyMs.Record(timing.QueueLatency.TotalMilliseconds);

        if (timing.GenerationLatency > TimeSpan.Zero)
            GenerationObservability.GenerationLatencyMs.Record(timing.GenerationLatency.TotalMilliseconds);

        if (timing.EvaluationLatency > TimeSpan.Zero)
            GenerationObservability.EvaluationLatencyMs.Record(timing.EvaluationLatency.TotalMilliseconds);

        if (timing.AcceptanceLatency > TimeSpan.Zero)
            GenerationObservability.AcceptanceLatencyMs.Record(timing.AcceptanceLatency.TotalMilliseconds);

        if (timing.TotalLatency > TimeSpan.Zero)
        {
            GenerationObservability.TotalLatencyMs.Record(timing.TotalLatency.TotalMilliseconds);
            GenerationObservability.ExecutionDurationMs.Record(timing.TotalLatency.TotalMilliseconds);
        }
    }
}
