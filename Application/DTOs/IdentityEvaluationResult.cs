using Domain.Enums;
using Domain.ValueObjects;

namespace Application.DTOs;

/// <summary>
/// Application DTO capturing the evaluated visual identity quality of a generated scene frame.
/// Bridges external evaluation infrastructure (e.g., multimodal CLIP whole-image embedding similarity proxy)
/// to application-level quality decisions and bounded mitigation escalation.
/// </summary>
public sealed record IdentityEvaluationResult(
    IdentityStatus Status,
    /// <summary>
    /// Whole-image CLIP identity similarity proxy score between generated frame and canonical avatar reference.
    /// Note: Measures overall visual identity retention (face, hair, attire, color scheme, palette), not face-isolated mesh.
    /// </summary>
    float IdentitySimilarity,
    float FeatureScore,
    bool InvariantViolated,
    float OverallScore,
    IReadOnlyList<IdentityViolation> Violations
)
{
    /// <summary>
    /// Obsolete backwards-compatibility alias for IdentitySimilarity.
    /// </summary>
    [Obsolete("Use IdentitySimilarity instead. Whole-image CLIP similarity proxy measures overall canonical visual identity (hair, attire, palette, traits), not isolated face mesh.", error: false)]
    public float FaceSimilarity => IdentitySimilarity;

    public static IdentityEvaluationResult Pass(float identitySimilarity, float featureScore, float overallScore) =>
        new(IdentityStatus.Passed, identitySimilarity, featureScore, false, overallScore, Array.Empty<IdentityViolation>());

    public static IdentityEvaluationResult Degrade(float identitySimilarity, float featureScore, float overallScore, IReadOnlyList<IdentityViolation> violations) =>
        new(IdentityStatus.Degraded, identitySimilarity, featureScore, false, overallScore, violations);

    public static IdentityEvaluationResult Fail(float identitySimilarity, float featureScore, float overallScore, IReadOnlyList<IdentityViolation> violations) =>
        new(IdentityStatus.Failed, identitySimilarity, featureScore, true, overallScore, violations);
}
