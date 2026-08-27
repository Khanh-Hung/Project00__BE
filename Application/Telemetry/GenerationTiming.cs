namespace Application.Telemetry;

/// <summary>
/// Authoritative timing breakdown capturing discrete latency milestones across the generation lifecycle.
/// </summary>
public sealed record GenerationTiming(
    TimeSpan QueueLatency,
    TimeSpan GenerationLatency,
    TimeSpan EvaluationLatency,
    TimeSpan AcceptanceLatency,
    TimeSpan TotalLatency
)
{
    public static GenerationTiming Zero => new(
        QueueLatency: TimeSpan.Zero,
        GenerationLatency: TimeSpan.Zero,
        EvaluationLatency: TimeSpan.Zero,
        AcceptanceLatency: TimeSpan.Zero,
        TotalLatency: TimeSpan.Zero
    );
}
