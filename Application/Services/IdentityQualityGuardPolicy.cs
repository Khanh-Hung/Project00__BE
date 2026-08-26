using System.Globalization;
using Application.DTOs;
using Application.Enums;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace Application.Services;

/// <summary>
/// Policy governing visual identity quality thresholds and bounded mitigation escalation.
/// Decides high-level mitigation action (Pass, RetryAttenuated, RetryIsolated, RejectDegraded)
/// without constructing workflow profiles or knowing about low-level model nodes.
/// </summary>
public sealed record IdentityQualityGuardPolicy
{
    public const int MaxAllowedAttempts = 3;

    /// <summary>
    /// Minimum acceptable whole-image CLIP identity similarity proxy score between generated frame and canonical avatar.
    /// </summary>
    public float MinAcceptableIdentitySimilarity { get; init; } = 0.75f;

    /// <summary>Backwards-compatible alias for MinAcceptableIdentitySimilarity.</summary>
    public float MinAcceptableFaceSimilarity => MinAcceptableIdentitySimilarity;

    public float MinAcceptableFeatureScore { get; init; } = 0.50f;
    public int MaxAttempts { get; init; } = 3;
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Configured evaluator type (e.g. "DevelopmentStub", "Http", "None").
    /// </summary>
    public string EvaluatorType { get; init; } = "DevelopmentStub";

    /// <summary>
    /// Explicit opt-in allowing development passthrough stub in Production environments.
    /// Default is false, ensuring fail-fast startup if QualityGuard is active without real evaluator.
    /// </summary>
    public bool AllowStubEvaluatorInProduction { get; init; } = false;

    public IdentityQualityGuardPolicy(
        float MinAcceptableIdentitySimilarity = 0.75f,
        float MinAcceptableFeatureScore = 0.50f,
        int MaxAttempts = 3,
        bool IsActive = true,
        string EvaluatorType = "DevelopmentStub",
        bool AllowStubEvaluatorInProduction = false,
        float? MinAcceptableFaceSimilarity = null)
    {
        float minIdentity = MinAcceptableFaceSimilarity ?? MinAcceptableIdentitySimilarity;
        if (MaxAttempts < 1 || MaxAttempts > MaxAllowedAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), $"MaxAttempts must be between 1 and {MaxAllowedAttempts}, but got {MaxAttempts}.");
        }
        if (minIdentity < 0.0f || minIdentity > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(MinAcceptableIdentitySimilarity), "MinAcceptableIdentitySimilarity must be between 0.0 and 1.0.");
        }
        if (MinAcceptableFeatureScore < 0.0f || MinAcceptableFeatureScore > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(MinAcceptableFeatureScore), "MinAcceptableFeatureScore must be between 0.0 and 1.0.");
        }

        this.MinAcceptableIdentitySimilarity = minIdentity;
        this.MinAcceptableFeatureScore = MinAcceptableFeatureScore;
        this.MaxAttempts = MaxAttempts;
        this.IsActive = IsActive;
        this.EvaluatorType = string.IsNullOrWhiteSpace(EvaluatorType) ? "DevelopmentStub" : EvaluatorType;
        this.AllowStubEvaluatorInProduction = AllowStubEvaluatorInProduction;
    }

    public static readonly IdentityQualityGuardPolicy Default = new();

    public static IdentityQualityGuardPolicy FromConfiguration(IConfiguration? configuration)
    {
        if (configuration == null) return Default;

        var minIdentityStr = configuration["AiProviders:ImageGeneration:QualityGuard:MinIdentitySimilarity"]
            ?? configuration["AiProviders:ImageGeneration:QualityGuard:MinFaceSimilarity"];
        float minIdentity = ParseValidatedFloat("QualityGuard:MinIdentitySimilarity", minIdentityStr, Default.MinAcceptableIdentitySimilarity);

        var minFeatStr = configuration["AiProviders:ImageGeneration:QualityGuard:MinFeatureScore"];
        float minFeat = ParseValidatedFloat("QualityGuard:MinFeatureScore", minFeatStr, Default.MinAcceptableFeatureScore);

        var maxAttemptsStr = configuration["AiProviders:ImageGeneration:QualityGuard:MaxAttempts"];
        int maxAttempts = ParseValidatedInt("QualityGuard:MaxAttempts", maxAttemptsStr, Default.MaxAttempts);

        var activeStr = configuration["AiProviders:ImageGeneration:QualityGuard:Enabled"]
            ?? configuration["AiProviders:ImageGeneration:QualityGuard:IsActive"];
        bool active = ParseValidatedBool("QualityGuard:Enabled", activeStr, Default.IsActive);

        var evaluatorTypeStr = configuration["AiProviders:ImageGeneration:QualityGuard:EvaluatorType"] ?? Default.EvaluatorType;

        var allowStubProdStr = configuration["AiProviders:ImageGeneration:QualityGuard:AllowStubEvaluatorInProduction"];
        bool allowStubProd = ParseValidatedBool("QualityGuard:AllowStubEvaluatorInProduction", allowStubProdStr, Default.AllowStubEvaluatorInProduction);

        return new IdentityQualityGuardPolicy(
            MinAcceptableIdentitySimilarity: minIdentity,
            MinAcceptableFeatureScore: minFeat,
            MaxAttempts: maxAttempts,
            IsActive: active,
            EvaluatorType: evaluatorTypeStr,
            AllowStubEvaluatorInProduction: allowStubProd
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

            // Minor identity/feature degradation on attempt 1 -> attenuate Slot 2
            return QualityMitigationAction.RetryAttenuated;
        }

        // Attempt 2 and above -> isolate Slot 1 (Slot 2 bypassed)
        return QualityMitigationAction.RetryIsolated;
    }

    public IdentityStatus EvaluateStatus(float identitySimilarity, float featureScore, bool invariantViolated, out List<IdentityViolation> violations)
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

        if (identitySimilarity < MinAcceptableIdentitySimilarity)
        {
            violations.Add(new IdentityViolation(
                ReferenceAuthorityScope.CanonicalIdentity,
                "IDENTITY_SIMILARITY_DEGRADED",
                $"Canonical identity similarity ({identitySimilarity:F4}) fell below threshold ({MinAcceptableIdentitySimilarity:F4}).",
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
