using Application.Common;
using Application.Interfaces;
using Domain.ValueObjects;

namespace Application.Services;

/// <summary>
/// Authoritative deterministic fingerprint implementation.
/// Covers: VisualSnapshot, GenerationProfile, DerivedSeed, AttemptNumber, WorkflowVersion, ModelIdentifier,
/// ConditioningConfiguration, Prompts, and MitigationAction.
/// Guarantees that identical inputs produce identical fingerprints regardless of machine, worker, or time,
/// and uses canonical JSON serialization to prevent delimiter collisions.
/// </summary>
public sealed class GenerationFingerprintService : IGenerationFingerprintService
{
    public string ComputeFingerprint(
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
        string? mitigationAction = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);

        var resolvedModel = !string.IsNullOrWhiteSpace(modelIdentifier)
            ? modelIdentifier
            : (!string.IsNullOrWhiteSpace(profile.Model) ? profile.Model : "ComfyUI/SDXL");

        var resolvedMitigation = mitigationAction ?? "Pass";

        return ComputeRawFingerprint(
            jobId: jobId,
            snapshotTurnId: snapshot.TurnId,
            sceneRevision: snapshot.SceneRevision,
            attemptNumber: attemptNumber,
            derivedSeed: derivedSeed,
            parametersJson: profile.ParametersJson ?? string.Empty,
            workflow: workflow,
            workflowVersion: workflowVersion,
            compiledPrompt: compiledPrompt,
            compiledNegativePrompt: compiledNegativePrompt,
            previousReferenceUrl: previousReferenceUrl,
            modelIdentifier: resolvedModel,
            mitigationAction: resolvedMitigation
        );
    }

    public string ComputeRawFingerprint(
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
        string? mitigationAction = null)
    {
        return DeterministicSeedDerivation.ComputeFingerprint(
            jobId: jobId,
            snapshotTurnId: snapshotTurnId,
            sceneRevision: sceneRevision,
            attemptNumber: attemptNumber,
            derivedSeed: derivedSeed,
            parametersJson: parametersJson,
            workflow: workflow,
            workflowVersion: workflowVersion,
            compiledPrompt: compiledPrompt,
            compiledNegativePrompt: compiledNegativePrompt,
            previousReferenceUrl: previousReferenceUrl,
            modelIdentifier: modelIdentifier,
            mitigationAction: mitigationAction
        );
    }
}
