using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Authoritative service computing unique, canonical, deterministic fingerprints for visual generation attempts.
/// Strict invariant: Independent of timestamps, worker IDs, and random GUIDs.
/// Identical logical generation inputs always produce identical fingerprints without delimiter collisions.
/// </summary>
public interface IGenerationFingerprintService
{
    string ComputeFingerprint(
        Guid jobId,
        VisualSnapshot snapshot,
        GenerationProfile profile,
        long derivedSeed,
        int attemptNumber,
        string workflow = "VisualIdentity",
        int workflowVersion = 1,
        string? modelIdentifier = null,
        string? compiledPrompt = null,
        string? compiledNegativePrompt = null,
        string? previousReferenceUrl = null,
        string? mitigationAction = null);

    string ComputeRawFingerprint(
        Guid jobId,
        Guid snapshotTurnId,
        int sceneRevision,
        int attemptNumber,
        long derivedSeed,
        string parametersJson,
        string workflow = "VisualIdentity",
        int workflowVersion = 1,
        string? compiledPrompt = null,
        string? compiledNegativePrompt = null,
        string? previousReferenceUrl = null,
        string? modelIdentifier = null,
        string? mitigationAction = null);
}
