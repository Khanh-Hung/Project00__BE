using Domain.Enums;
using Domain.ValueObjects;

namespace Application.DTOs;

/// <summary>
/// Application DTO capturing the evaluated identity quality of a generated scene frame.
/// Bridges external evaluation infrastructure to application-level quality decisions.
/// </summary>
public sealed record IdentityEvaluationResult(
    IdentityStatus Status,
    float FaceSimilarity,
    float FeatureScore,
    bool InvariantViolated,
    float OverallScore,
    IReadOnlyList<IdentityViolation> Violations
)
{
    public static IdentityEvaluationResult Pass(float faceSimilarity, float featureScore, float overallScore) =>
        new(IdentityStatus.Passed, faceSimilarity, featureScore, false, overallScore, Array.Empty<IdentityViolation>());

    public static IdentityEvaluationResult Degrade(float faceSimilarity, float featureScore, float overallScore, IReadOnlyList<IdentityViolation> violations) =>
        new(IdentityStatus.Degraded, faceSimilarity, featureScore, false, overallScore, violations);

    public static IdentityEvaluationResult Fail(float faceSimilarity, float featureScore, float overallScore, IReadOnlyList<IdentityViolation> violations) =>
        new(IdentityStatus.Failed, faceSimilarity, featureScore, true, overallScore, violations);
}
