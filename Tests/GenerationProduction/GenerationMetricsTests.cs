using Application.Interfaces;
using Application.Telemetry;
using Domain.Enums;
using Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class GenerationMetricsTests
{
    private readonly IGenerationMetrics _metrics = new GenerationMetrics(NullLogger<GenerationMetrics>.Instance);

    [Fact]
    public void GenerationMetrics_RecordGenerationStarted_ExecutesCleanly()
    {
        var jobId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        var exception = Record.Exception(() => _metrics.RecordGenerationStarted(jobId, requestId));
        Assert.Null(exception);
    }

    [Fact]
    public void GenerationMetrics_RecordGenerationCompleted_ExecutesCleanly()
    {
        var jobId = Guid.NewGuid();
        var timing = new GenerationTiming(
            QueueLatency: TimeSpan.FromMilliseconds(20),
            GenerationLatency: TimeSpan.FromMilliseconds(800),
            EvaluationLatency: TimeSpan.FromMilliseconds(150),
            AcceptanceLatency: TimeSpan.FromMilliseconds(30),
            TotalLatency: TimeSpan.FromMilliseconds(1000)
        );

        var exception = Record.Exception(() => _metrics.RecordGenerationCompleted(jobId, attempts: 1, timing));
        Assert.Null(exception);
    }

    [Fact]
    public void GenerationMetrics_RecordGenerationFailed_ExecutesCleanly()
    {
        var jobId = Guid.NewGuid();

        var exception = Record.Exception(() => _metrics.RecordGenerationFailed(
            jobId,
            GenerationFailureCategory.ProviderTimeout,
            attempts: 2,
            totalDuration: TimeSpan.FromSeconds(15)
        ));

        Assert.Null(exception);
    }

    [Fact]
    public void GenerationMetrics_RecordGenerationQuarantined_ExecutesCleanly()
    {
        var jobId = Guid.NewGuid();

        var exception = Record.Exception(() => _metrics.RecordGenerationQuarantined(
            jobId,
            attempts: 3,
            finalSimilarity: 0.65f,
            finalFeatureScore: 0.42f
        ));

        Assert.Null(exception);
    }

    [Fact]
    public void GenerationMetrics_RecordIdentityEvaluation_HandlesDirectPass_DegradedRetry_AndRecovery()
    {
        var jobId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        // 1. Direct pass (Attempt 1) -> does not trigger retry
        var ex1 = Record.Exception(() => _metrics.RecordIdentityEvaluation(
            jobId, attemptId, attemptNumber: 1, identitySimilarity: 0.85f, featureScore: 0.70f, passed: true, willRetry: false, duration: TimeSpan.FromMilliseconds(120)));
        Assert.Null(ex1);

        // 2. Degraded attempt escalating to retry -> triggers IdentityGuardRetryTotal
        var ex2 = Record.Exception(() => _metrics.RecordIdentityEvaluation(
            jobId, attemptId, attemptNumber: 1, identitySimilarity: 0.68f, featureScore: 0.45f, passed: false, willRetry: true, duration: TimeSpan.FromMilliseconds(125)));
        Assert.Null(ex2);

        // 3. Attempt 2 recovery pass -> triggers IdentityGuardRecoveryTotal
        var ex3 = Record.Exception(() => _metrics.RecordIdentityEvaluation(
            jobId, attemptId, attemptNumber: 2, identitySimilarity: 0.88f, featureScore: 0.75f, passed: true, willRetry: false, duration: TimeSpan.FromMilliseconds(130)));
        Assert.Null(ex3);

        // 4. Exhausted attempt quarantined -> triggers quarantine
        var ex4 = Record.Exception(() => _metrics.RecordIdentityEvaluation(
            jobId, attemptId, attemptNumber: 3, identitySimilarity: 0.62f, featureScore: 0.40f, passed: false, willRetry: false, duration: TimeSpan.FromMilliseconds(135)));
        Assert.Null(ex4);
    }

    [Fact]
    public void GenerationMetrics_RecordTiming_HandlesZeroAndNonZeroTimings()
    {
        var zero = GenerationTiming.Zero;
        var exZero = Record.Exception(() => _metrics.RecordTiming(zero));
        Assert.Null(exZero);

        var normal = new GenerationTiming(
            QueueLatency: TimeSpan.FromMilliseconds(5),
            GenerationLatency: TimeSpan.FromMilliseconds(500),
            EvaluationLatency: TimeSpan.FromMilliseconds(100),
            AcceptanceLatency: TimeSpan.FromMilliseconds(20),
            TotalLatency: TimeSpan.FromMilliseconds(625)
        );

        var exNormal = Record.Exception(() => _metrics.RecordTiming(normal));
        Assert.Null(exNormal);
    }
}
