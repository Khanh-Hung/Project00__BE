using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Service responsible for diagnosing and reconciling visual session state integrity.
/// Guarantees that VisualSessionState always points to an accepted, non-quarantined SceneImage
/// backed by an authoritative ImageGenerationAttempt record.
/// </summary>
public interface IVisualStateConsistencyService
{
    /// <summary>
    /// Evaluates the consistency of a session's visual state against the authoritative attempt ledger.
    /// Does not mutate database state.
    /// </summary>
    Task<ArtifactConsistencyResult> ValidateConsistencyAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Explicitly and deterministically repairs an inconsistent visual session state when an authoritative
    /// winning attempt and artifact can be unequivocally proven.
    /// Throws InvalidOperationException if the state is Corrupted or cannot be proven.
    /// </summary>
    Task<ArtifactConsistencyResult> RepairVisualStateAsync(Guid sessionId, CancellationToken ct = default);
}
