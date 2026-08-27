using System.Diagnostics.Metrics;

namespace Application.Common;

/// <summary>
/// Centralized observability instruments, metric counters, and structured correlation tags
/// for the generation runtime. Production-ready OpenTelemetry Meter.
/// </summary>
public static class GenerationObservability
{
    public const string MeterName = "Project00.GenerationRuntime";
    private static readonly Meter s_meter = new(MeterName, "1.0.0");

    // Standard Counters
    public static readonly Counter<long> RequestsTotal = s_meter.CreateCounter<long>(
        "generation_requests_total",
        description: "Total number of generation requests initiated");

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

    public static readonly Counter<long> AttemptsTotal = s_meter.CreateCounter<long>(
        "generation_attempts_total",
        description: "Total number of individual generation attempts executed across all jobs");

    public static readonly Counter<long> RetriesTotal = s_meter.CreateCounter<long>(
        "generation_retries_total",
        description: "Total number of generation retries scheduled");

    public static readonly Counter<long> RecoveriesTotal = s_meter.CreateCounter<long>(
        "generation_recoveries_total",
        description: "Total number of expired worker leases recovered");

    public static readonly Counter<long> OrphanArtifactsTotal = s_meter.CreateCounter<long>(
        "generation_orphan_artifacts_total",
        description: "Total number of orphan artifacts reconciled");

    // Identity Evaluation & Quality Guard Counters
    public static readonly Counter<long> IdentityEvaluationTotal = s_meter.CreateCounter<long>(
        "identity_evaluation_total",
        description: "Total number of individual visual quality evaluations executed");

    public static readonly Counter<long> IdentityGuardRetryTotal = s_meter.CreateCounter<long>(
        "identity_guard_retry_total",
        description: "Total number of degraded frames escalating to mitigation retries");

    public static readonly Counter<long> IdentityGuardRecoveryTotal = s_meter.CreateCounter<long>(
        "identity_guard_recovery_total",
        description: "Total number of identity guard retries that successfully recovered to Passed");

    public static readonly Counter<long> IdentityGuardQuarantineTotal = s_meter.CreateCounter<long>(
        "identity_guard_quarantine_total",
        description: "Total number of identity guard evaluations leading to frame quarantine");

    // Backward-compatibility alias
    [Obsolete("Use IdentityGuardRetryTotal (when mitigation triggers) or IdentityEvaluationTotal (for all evaluations) explicitly.")]
    public static readonly Counter<long> IdentityGuardTriggerTotal = IdentityGuardRetryTotal;

    // Granular Stage Latency Histograms (ms)
    public static readonly Histogram<double> QueueLatencyMs = s_meter.CreateHistogram<double>(
        "generation_queue_latency_ms",
        unit: "ms",
        description: "Duration a generation item waited in queue before execution acquisition in milliseconds");

    public static readonly Histogram<double> GenerationLatencyMs = s_meter.CreateHistogram<double>(
        "generation_generation_latency_ms",
        unit: "ms",
        description: "Duration of provider generation (ComfyUI GPU execution) in milliseconds");

    public static readonly Histogram<double> EvaluationLatencyMs = s_meter.CreateHistogram<double>(
        "generation_evaluation_latency_ms",
        unit: "ms",
        description: "Duration of identity and quality evaluation in milliseconds");

    public static readonly Histogram<double> AcceptanceLatencyMs = s_meter.CreateHistogram<double>(
        "generation_acceptance_latency_ms",
        unit: "ms",
        description: "Duration of atomic CAS acceptance and artifact lineage promotion in milliseconds");

    public static readonly Histogram<double> TotalLatencyMs = s_meter.CreateHistogram<double>(
        "generation_total_latency_ms",
        unit: "ms",
        description: "End-to-end total generation pipeline latency in milliseconds");

    // Backward-compatibility aliases
    public static readonly Histogram<double> ExecutionDurationMs = s_meter.CreateHistogram<double>(
        "generation_execution_duration_ms",
        unit: "ms",
        description: "Duration of generation execution in milliseconds");

    public static readonly Histogram<double> QueueWaitDurationMs = s_meter.CreateHistogram<double>(
        "generation_queue_wait_duration_ms",
        unit: "ms",
        description: "Duration a job waited in queue before worker acquisition in milliseconds");
}
