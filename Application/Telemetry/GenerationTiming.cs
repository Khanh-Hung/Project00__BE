namespace Application.Telemetry;

/// <summary>
/// Structured telemetry record capturing discrete latency phases across the generation pipeline.
/// Invariant: All phase durations must be non-negative.
/// </summary>
public sealed record GenerationTiming
{
    public TimeSpan QueueLatency { get; init; }
    public TimeSpan GenerationLatency { get; init; }
    public TimeSpan EvaluationLatency { get; init; }
    public TimeSpan AcceptanceLatency { get; init; }
    public TimeSpan TotalLatency { get; init; }

    public static GenerationTiming Zero => new(
        QueueLatency: TimeSpan.Zero,
        GenerationLatency: TimeSpan.Zero,
        EvaluationLatency: TimeSpan.Zero,
        AcceptanceLatency: TimeSpan.Zero,
        TotalLatency: TimeSpan.Zero
    );

    public GenerationTiming(
        TimeSpan QueueLatency,
        TimeSpan GenerationLatency,
        TimeSpan EvaluationLatency,
        TimeSpan AcceptanceLatency,
        TimeSpan TotalLatency)
    {
        if (QueueLatency < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(QueueLatency), "QueueLatency cannot be negative.");
        if (GenerationLatency < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(GenerationLatency), "GenerationLatency cannot be negative.");
        if (EvaluationLatency < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(EvaluationLatency), "EvaluationLatency cannot be negative.");
        if (AcceptanceLatency < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(AcceptanceLatency), "AcceptanceLatency cannot be negative.");
        if (TotalLatency < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(TotalLatency), "TotalLatency cannot be negative.");

        this.QueueLatency = QueueLatency;
        this.GenerationLatency = GenerationLatency;
        this.EvaluationLatency = EvaluationLatency;
        this.AcceptanceLatency = AcceptanceLatency;
        this.TotalLatency = TotalLatency;
    }
}
