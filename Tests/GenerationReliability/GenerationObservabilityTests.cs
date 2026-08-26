using Application.Common;
using Xunit;

namespace Tests.GenerationReliability;

public sealed class GenerationObservabilityTests
{
    [Fact]
    public void ObservabilityInstruments_AreProperlyConfigured()
    {
        Assert.Equal("Project00.GenerationRuntime", GenerationObservability.MeterName);
        Assert.NotNull(GenerationObservability.JobsTotal);
        Assert.NotNull(GenerationObservability.JobsCompletedTotal);
        Assert.NotNull(GenerationObservability.JobsFailedTotal);
        Assert.NotNull(GenerationObservability.JobsCancelledTotal);
        Assert.NotNull(GenerationObservability.JobsQuarantinedTotal);
        Assert.NotNull(GenerationObservability.RetriesTotal);
        Assert.NotNull(GenerationObservability.RecoveriesTotal);
        Assert.NotNull(GenerationObservability.OrphanArtifactsTotal);
        Assert.NotNull(GenerationObservability.ExecutionDurationMs);
        Assert.NotNull(GenerationObservability.QueueWaitDurationMs);
    }

    [Fact]
    public void MetricCounters_CanRecordAndIncrementWithoutThrowing()
    {
        GenerationObservability.JobsTotal.Add(1);
        GenerationObservability.JobsCompletedTotal.Add(1);
        GenerationObservability.RetriesTotal.Add(1);
        GenerationObservability.ExecutionDurationMs.Record(125.5);
    }
}
