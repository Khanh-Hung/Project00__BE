namespace Domain.Enums;

/// <summary>
/// Diagnostic status representing the relational and semantic consistency of a ChatSession's visual state.
/// </summary>
public enum VisualStateConsistencyStatus
{
    /// <summary>
    /// VisualSessionState points to a valid, active SceneImage whose lineage, session, and revision perfectly match the accepted generation attempt.
    /// </summary>
    Healthy = 0,

    /// <summary>
    /// Visual state violates expected invariants (e.g. CurrentImageId is null when a completed accepted job exists, or artifact is not marked current).
    /// </summary>
    Inconsistent = 1,

    /// <summary>
    /// Inconsistent state that can be deterministically reconstructed from authoritative ImageGenerationAttempt ledgers without guessing.
    /// </summary>
    Repairable = 2,

    /// <summary>
    /// Visual state references foreign session artifacts, conflicting revisions, or has missing/unrecoverable provenance records.
    /// </summary>
    Corrupted = 3
}
