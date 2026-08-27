using System.Security.Cryptography;
using System.Text.Json;

namespace Application.Common;

/// <summary>
/// Provides pure, deterministic seed derivation and canonical generation attempt fingerprinting.
/// Guarantees exact reproducibility and idempotency across retry attempts without delimiter collision risk.
/// </summary>
public static class DeterministicSeedDerivation
{
    public const string DefaultModel = "meinamix_meinaV11.safetensors";
    public const string DefaultWorkflow = "VisualIdentity";
    public const int DefaultWorkflowVersion = 1;
    public const string DefaultMitigationAction = "Pass";

    /// <summary>
    /// Derives a deterministic seed for a given attempt number from the base seed.
    /// Attempt 1 returns baseSeed unmodified.
    /// Attempts > 1 return a deterministic permutation using 64-bit SplitMix.
    /// </summary>
    public static long Derive(long baseSeed, int attemptNumber)
    {
        if (attemptNumber <= 1)
            return baseSeed;

        ulong z = (ulong)baseSeed + ((ulong)attemptNumber * 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        ulong result = z ^ (z >> 31);

        // Map to positive signed 64-bit integer
        return (long)(result & 0x7FFFFFFFFFFFFFFFUL);
    }

    /// <summary>
    /// Computes a unique, canonical, collision-safe SHA-256 fingerprint for a generation attempt covering all
    /// authoritative generation request inputs:
    /// JobId, TurnId, SceneRevision, AttemptNumber, DerivedSeed, Workflow, WorkflowVersion, ModelIdentifier, ParametersJson,
    /// CompiledPrompt, CompiledNegativePrompt, PreviousReferenceUrl, and MitigationAction.
    /// Uses canonical JSON serialization to prevent delimiter collision vulnerabilities.
    /// </summary>
    public static string ComputeFingerprint(
        Guid jobId,
        Guid snapshotTurnId,
        int sceneRevision,
        int attemptNumber,
        long derivedSeed,
        string parametersJson,
        string workflow = DefaultWorkflow,
        int workflowVersion = DefaultWorkflowVersion,
        string? compiledPrompt = null,
        string? compiledNegativePrompt = null,
        string? previousReferenceUrl = null,
        string? modelIdentifier = null,
        string? mitigationAction = null)
    {
        var canonicalPayload = new
        {
            jobId = jobId.ToString("D"),
            snapshotTurnId = snapshotTurnId.ToString("D"),
            sceneRevision = sceneRevision,
            attemptNumber = attemptNumber,
            derivedSeed = derivedSeed,
            workflow = !string.IsNullOrWhiteSpace(workflow) ? workflow : DefaultWorkflow,
            workflowVersion = workflowVersion > 0 ? workflowVersion : DefaultWorkflowVersion,
            modelIdentifier = !string.IsNullOrWhiteSpace(modelIdentifier) ? modelIdentifier : DefaultModel,
            parametersJson = parametersJson ?? string.Empty,
            compiledPrompt = compiledPrompt ?? string.Empty,
            compiledNegativePrompt = compiledNegativePrompt ?? string.Empty,
            previousReferenceUrl = previousReferenceUrl ?? string.Empty,
            mitigationAction = !string.IsNullOrWhiteSpace(mitigationAction) ? mitigationAction : DefaultMitigationAction
        };

        var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(canonicalPayload);
        var hashBytes = SHA256.HashData(canonicalBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
