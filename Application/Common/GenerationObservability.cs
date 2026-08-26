using System.Diagnostics.Metrics;

namespace Application.Common;

/// <summary>
/// Centralized observability instruments, metric counters, and structured correlation tags
/// for the generation runtime.
/// </summary>
public static class GenerationObservability
{
    public const string MeterName = "Project00.GenerationRuntime";
    private static readonly Meter s_meter = new(MeterName, "1.0.0");

    // Standard Metrics
    public static readonly Counter<long> JobsTotal = s_meter.CreateCounter<long>(
        "generation_jobs_total",
        description: "Total number of generation jobs processed");

    public static readonly Counter<long> JobsCompletedTotal = s_meter.CreateCounter<long>(
        "generation_jobs_completed_total",
        description: "Total number of generation jobs completed successfully");

    public static readonly Counter<long> JobsFailedTotal = s_meter.CreateCounter<long>(
        "generation_jobs_failed_total",
        description: "Total number of generation jobs failed");

    public static readonly Counter<long> JobsCancelledTotal = s_meter.CreateCounter<long>(
        "generation_jobs_cancelled_total",
        description: "Total number of generation jobs cancelled");

    public static readonly Counter<long> JobsQuarantinedTotal = s_meter.CreateCounter<long>(
        "generation_jobs_quarantined_total",
        description: "Total number of generation jobs quarantined due to quality gate failure");

    public static readonly Counter<long> RetriesTotal = s_meter.CreateCounter<long>(
        "generation_retries_total",
        description: "Total number of generation retries scheduled");

    public static readonly Counter<long> RecoveriesTotal = s_meter.CreateCounter<long>(
        "generation_recoveries_total",
        description: "Total number of expired worker leases recovered");

    public static readonly Counter<long> OrphanArtifactsTotal = s_meter.CreateCounter<long>(
        "generation_orphan_artifacts_total",
        description: "Total number of orphan artifacts reconciled");

    public static readonly Histogram<double> ExecutionDurationMs = s_meter.CreateHistogram<double>(
        "generation_execution_duration_ms",
        unit: "ms",
        description: "Duration of generation execution in milliseconds");

    public static readonly Histogram<double> QueueWaitDurationMs = s_meter.CreateHistogram<double>(
        "generation_queue_wait_duration_ms",
        unit: "ms",
        description: "Duration a job waited in queue before worker acquisition in milliseconds");
}
