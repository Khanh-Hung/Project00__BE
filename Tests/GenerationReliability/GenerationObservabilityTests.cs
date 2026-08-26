using Application.Common;
using System.Diagnostics.Metrics;
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
    public void MeterListener_RecordsEmittedMeasurementsAccurately()
    {
        long observedJobsTotal = 0;
        long observedCompleted = 0;
        long observedRecoveries = 0;
        double observedDuration = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == GenerationObservability.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "generation_jobs_total")
                observedJobsTotal += measurement;
            else if (instrument.Name == "generation_jobs_completed_total")
                observedCompleted += measurement;
            else if (instrument.Name == "generation_recoveries_total")
                observedRecoveries += measurement;
        });

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "generation_execution_duration_ms")
                observedDuration += measurement;
        });

        listener.Start();

        // Emit telemetry measurements
        GenerationObservability.JobsTotal.Add(5);
        GenerationObservability.JobsCompletedTotal.Add(3);
        GenerationObservability.RecoveriesTotal.Add(2);
        GenerationObservability.ExecutionDurationMs.Record(250.0);

        listener.RecordObservableInstruments();

        Assert.Equal(5, observedJobsTotal);
        Assert.Equal(3, observedCompleted);
        Assert.Equal(2, observedRecoveries);
        Assert.Equal(250.0, observedDuration);
    }
}
