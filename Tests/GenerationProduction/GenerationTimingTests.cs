using Application.Telemetry;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class GenerationTimingTests
{
    [Fact]
    public void GenerationTiming_RecordCapturesMonotonicDurationsCorrectly()
    {
        var queueLatency = TimeSpan.FromMilliseconds(15.2);
        var genLatency = TimeSpan.FromMilliseconds(1250.4);
        var evalLatency = TimeSpan.FromMilliseconds(210.8);
        var acceptLatency = TimeSpan.FromMilliseconds(35.1);
        var totalLatency = TimeSpan.FromMilliseconds(1511.5);

        var timing = new GenerationTiming(
            QueueLatency: queueLatency,
            GenerationLatency: genLatency,
            EvaluationLatency: evalLatency,
            AcceptanceLatency: acceptLatency,
            TotalLatency: totalLatency
        );

        Assert.True(timing.QueueLatency >= TimeSpan.Zero);
        Assert.True(timing.GenerationLatency >= TimeSpan.Zero);
        Assert.True(timing.EvaluationLatency >= TimeSpan.Zero);
        Assert.True(timing.AcceptanceLatency >= TimeSpan.Zero);
        Assert.True(timing.TotalLatency >= TimeSpan.Zero);

        // Invariant: Total latency covers individual stage latency components
        var sumOfStages = timing.QueueLatency + timing.GenerationLatency + timing.EvaluationLatency + timing.AcceptanceLatency;
        Assert.True(timing.TotalLatency >= sumOfStages - TimeSpan.FromMilliseconds(1.0));
    }

    [Fact]
    public void GenerationTiming_Zero_ProvidesSafeDefaults()
    {
        var zero = GenerationTiming.Zero;

        Assert.Equal(TimeSpan.Zero, zero.QueueLatency);
        Assert.Equal(TimeSpan.Zero, zero.GenerationLatency);
        Assert.Equal(TimeSpan.Zero, zero.EvaluationLatency);
        Assert.Equal(TimeSpan.Zero, zero.AcceptanceLatency);
        Assert.Equal(TimeSpan.Zero, zero.TotalLatency);
    }
}
