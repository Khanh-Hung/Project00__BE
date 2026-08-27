namespace Domain.ValueObjects;

/// <summary>
/// Immutable value object representing the resolved visual predecessor for conditioning Slot 2 (Scene Continuity).
/// Invariants: Never references a Quarantined, Failed, Cancelled, or Cross-Session artifact.
/// </summary>
public sealed record VisualPredecessor
{
    public Guid? ArtifactId { get; init; }
    public string ImageUrl { get; init; }
    public string Source { get; init; }
    public int? VisualRevision { get; init; }

    public VisualPredecessor(
        Guid? artifactId,
        string imageUrl,
        string source,
        int? visualRevision = null)
    {
        ArtifactId = artifactId;
        ImageUrl = imageUrl ?? throw new ArgumentNullException(nameof(imageUrl));
        Source = source ?? "Unknown";
        VisualRevision = visualRevision;
    }
}
