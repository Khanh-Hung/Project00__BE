using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Application.Common;

/// <summary>
/// Provides pure, deterministic seed derivation and generation attempt fingerprinting.
/// Guarantees exact reproducibility and idempotency across retry attempts.
/// </summary>
public static class DeterministicSeedDerivation
{
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
    /// Computes a unique, canonical, deterministic fingerprint for a generation attempt covering all
    /// authoritative generation request inputs:
    /// JobId, TurnId, SceneRevision, AttemptNumber, DerivedSeed, Workflow, WorkflowVersion, ModelIdentifier, ParametersJson,
    /// CompiledPrompt, CompiledNegativePrompt, PreviousReferenceUrl, and MitigationAction.
    /// Used to enforce strict DB-level uniqueness and idempotency across worker retries.
    /// </summary>
    public static string ComputeFingerprint(
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
        var rawKey = string.Join("|",
            jobId.ToString("N"),
            snapshotTurnId.ToString("N"),
            sceneRevision.ToString(CultureInfo.InvariantCulture),
            attemptNumber.ToString(CultureInfo.InvariantCulture),
            derivedSeed.ToString(CultureInfo.InvariantCulture),
            workflow ?? string.Empty,
            workflowVersion.ToString(CultureInfo.InvariantCulture),
            modelIdentifier ?? "ComfyUI/SDXL",
            parametersJson ?? string.Empty,
            compiledPrompt ?? string.Empty,
            compiledNegativePrompt ?? string.Empty,
            previousReferenceUrl ?? string.Empty,
            mitigationAction ?? "Pass");

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
