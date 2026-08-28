namespace Domain.Enums;

/// <summary>
/// Result status of evaluating a generated scene frame against canonical character identity invariants.
/// </summary>
public enum IdentityStatus
{
    /// <summary>
    /// Frame satisfies all hard invariants, face identity thresholds, and signature feature criteria.
    /// </summary>
    Passed = 1,

    /// <summary>
    /// Minor drift in face or feature metrics, but no critical invariant violations.
    /// </summary>
    Degraded = 2,

    /// <summary>
    /// Hard invariant violation (e.g. gender presentation mismatch, missing signature feature, or unacceptable face distortion).
    /// </summary>
    Failed = 3
}
