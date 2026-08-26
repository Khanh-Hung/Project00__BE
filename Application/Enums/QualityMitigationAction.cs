namespace Application.Enums;

/// <summary>
/// Mitigation action decided by IdentityQualityGuardPolicy in response to an evaluated generation attempt.
/// </summary>
public enum QualityMitigationAction
{
    /// <summary>
    /// Frame satisfies quality thresholds. Accepted.
    /// </summary>
    Pass = 1,

    /// <summary>
    /// Minor degradation. Retry with attenuated Slot 2 weight and deterministic seed derivation.
    /// </summary>
    RetryAttenuated = 2,

    /// <summary>
    /// Severe degradation or invariant violation. Retry with isolated Slot 1 (Slot 2 completely bypassed).
    /// </summary>
    RetryIsolated = 3,

    /// <summary>
    /// Max retry attempts exhausted without reaching acceptable quality. Reject frame and quarantine from continuity state.
    /// </summary>
    RejectDegraded = 4
}
