using System.Globalization;
using Application.DTOs;
using Application.Enums;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace Application.Services;

/// <summary>
/// Policy governing identity quality thresholds and bounded mitigation escalation.
/// Decides high-level mitigation action (Pass, RetryAttenuated, RetryIsolated, RejectDegraded)
/// without constructing workflow profiles or knowing about low-level model nodes.
/// </summary>
public sealed record IdentityQualityGuardPolicy
{
    public const int MaxAllowedAttempts = 3;

    public float MinAcceptableFaceSimilarity { get; init; } = 0.75f;
    public float MinAcceptableFeatureScore { get; init; } = 0.50f;
    public int MaxAttempts { get; init; } = 3;
    public bool IsActive { get; init; } = true;

    public IdentityQualityGuardPolicy(
        float MinAcceptableFaceSimilarity = 0.75f,
        float MinAcceptableFeatureScore = 0.50f,
        int MaxAttempts = 3,
        bool IsActive = true)
    {
        if (MaxAttempts < 1 || MaxAttempts > MaxAllowedAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), $"MaxAttempts must be between 1 and {MaxAllowedAttempts}, but got {MaxAttempts}.");
        }
        if (MinAcceptableFaceSimilarity < 0.0f || MinAcceptableFaceSimilarity > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(MinAcceptableFaceSimilarity), "MinAcceptableFaceSimilarity must be between 0.0 and 1.0.");
        }
        if (MinAcceptableFeatureScore < 0.0f || MinAcceptableFeatureScore > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(MinAcceptableFeatureScore), "MinAcceptableFeatureScore must be between 0.0 and 1.0.");
        }

        this.MinAcceptableFaceSimilarity = MinAcceptableFaceSimilarity;
        this.MinAcceptableFeatureScore = MinAcceptableFeatureScore;
        this.MaxAttempts = MaxAttempts;
        this.IsActive = IsActive;
    }

    public static readonly IdentityQualityGuardPolicy Default = new();

    public static IdentityQualityGuardPolicy FromConfiguration(IConfiguration? configuration)
    {
        if (configuration == null) return Default;

        var minFaceStr = configuration["AiProviders:ImageGeneration:QualityGuard:MinFaceSimilarity"];
        float minFace = ParseValidatedFloat("QualityGuard:MinFaceSimilarity", minFaceStr, Default.MinAcceptableFaceSimilarity);

        var minFeatStr = configuration["AiProviders:ImageGeneration:QualityGuard:MinFeatureScore"];
        float minFeat = ParseValidatedFloat("QualityGuard:MinFeatureScore", minFeatStr, Default.MinAcceptableFeatureScore);

        var maxAttemptsStr = configuration["AiProviders:ImageGeneration:QualityGuard:MaxAttempts"];
        int maxAttempts = ParseValidatedInt("QualityGuard:MaxAttempts", maxAttemptsStr, Default.MaxAttempts);

        var activeStr = configuration["AiProviders:ImageGeneration:QualityGuard:Enabled"]
            ?? configuration["AiProviders:ImageGeneration:QualityGuard:IsActive"];
        bool active = ParseValidatedBool("QualityGuard:Enabled", activeStr, Default.IsActive);

        return new IdentityQualityGuardPolicy(
            MinAcceptableFaceSimilarity: minFace,
            MinAcceptableFeatureScore: minFeat,
            MaxAttempts: maxAttempts,
            IsActive: active
        );
    }

    private static float ParseValidatedFloat(string keyName, string? valueStr, float defaultValue)
    {
        if (string.IsNullOrWhiteSpace(valueStr)) return defaultValue;
        if (!float.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || float.IsNaN(parsed) || float.IsInfinity(parsed) || parsed < 0.0f || parsed > 1.0f)
        {
            throw new InvalidOperationException($"Invalid configuration for '{keyName}': '{valueStr}'. Must be a float between 0.0 and 1.0.");
        }
        return parsed;
    }

    private static int ParseValidatedInt(string keyName, string? valueStr, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(valueStr)) return defaultValue;
        if (!int.TryParse(valueStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1 || parsed > MaxAllowedAttempts)
        {
            throw new InvalidOperationException($"Invalid configuration for '{keyName}': '{valueStr}'. Must be an integer between 1 and {MaxAllowedAttempts}.");
        }
        return parsed;
    }

    private static bool ParseValidatedBool(string keyName, string? valueStr, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(valueStr)) return defaultValue;
        if (!bool.TryParse(valueStr, out var parsed))
        {
            throw new InvalidOperationException($"Invalid configuration for '{keyName}': '{valueStr}'. Must be boolean.");
        }
        return parsed;
    }

    public QualityMitigationAction DecideMitigation(int currentAttempt, IdentityEvaluationResult evaluation)
    {
        if (!IsActive || evaluation.Status == IdentityStatus.Passed)
        {
            return QualityMitigationAction.Pass;
        }

        if (currentAttempt >= MaxAttempts)
        {
            return QualityMitigationAction.RejectDegraded;
        }

        if (currentAttempt == 1)
        {
            // Severe hard invariant violation on attempt 1 -> immediately isolate Slot 1
            if (evaluation.Status == IdentityStatus.Failed || evaluation.InvariantViolated)
            {
                return QualityMitigationAction.RetryIsolated;
            }

            // Minor face/feature degradation on attempt 1 -> attenuate Slot 2
            return QualityMitigationAction.RetryAttenuated;
        }

        // Attempt 2 and above -> isolate Slot 1 (Slot 2 bypassed)
        return QualityMitigationAction.RetryIsolated;
    }

    public IdentityStatus EvaluateStatus(float faceSimilarity, float featureScore, bool invariantViolated, out List<IdentityViolation> violations)
    {
        violations = new List<IdentityViolation>();

        if (invariantViolated)
        {
            violations.Add(new IdentityViolation(
                ReferenceAuthorityScope.CanonicalIdentity,
                "INVARIANT_VIOLATION",
                "Hard gender or physical invariant was violated in generated output.",
                IsCritical: true));
            return IdentityStatus.Failed;
        }

        if (faceSimilarity < MinAcceptableFaceSimilarity)
        {
            violations.Add(new IdentityViolation(
                ReferenceAuthorityScope.CanonicalIdentity,
                "FACE_SIMILARITY_DEGRADED",
                $"Face similarity ({faceSimilarity:F4}) fell below threshold ({MinAcceptableFaceSimilarity:F4}).",
                IsCritical: false));
        }

        if (featureScore < MinAcceptableFeatureScore)
        {
            violations.Add(new IdentityViolation(
                ReferenceAuthorityScope.CanonicalIdentity,
                "FEATURE_RETENTION_DEGRADED",
                $"Signature feature score ({featureScore:F4}) fell below threshold ({MinAcceptableFeatureScore:F4}).",
                IsCritical: false));
        }

        return violations.Count == 0 ? IdentityStatus.Passed : IdentityStatus.Degraded;
    }
}
