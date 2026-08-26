namespace Application.Interfaces;

/// <summary>
/// Authoritative resolver for predecessor scene visual reference lineage.
/// Invariant (P0-2): Predecessor image references are resolved strictly from confirmed,
/// accepted artifacts (IsCurrent = true) in the lineage, never from unconfirmed or in-flight candidates.
/// </summary>
public interface IPredecessorLineageResolver
{
    Task<(bool IsReady, string? PredecessorImageUrl, string? DeferReason)> ResolvePredecessorReferenceAsync(
        Guid sessionId,
        int currentRevision,
        int? explicitPredecessorRevision,
        string? fallbackImageUrl,
        CancellationToken ct = default);
}
