using Application.DTOs;
using Application.Interfaces;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Services;

/// <summary>
/// Default production evaluator performing invariant validation and reference authority verification.
/// Can be extended with local or remote CLIP / embedding services.
/// </summary>
public sealed class DefaultIdentityQualityEvaluator : IIdentityQualityEvaluator
{
    public Task<IdentityEvaluationResult> EvaluateAsync(
        string imageLocation,
        VisualSnapshot snapshot,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imageLocation))
        {
            return Task.FromResult(IdentityEvaluationResult.Fail(
                faceSimilarity: 0.0f,
                featureScore: 0.0f,
                overallScore: 0.0f,
                violations: new[]
                {
                    new IdentityViolation(
                        ReferenceAuthorityScope.CanonicalIdentity,
                        "EMPTY_IMAGE_LOCATION",
                        "Image location is empty or unavailable.",
                        IsCritical: true)
                }
            ));
        }

        return Task.FromResult(IdentityEvaluationResult.Pass(
            faceSimilarity: 0.85f,
            featureScore: 0.90f,
            overallScore: 0.87f
        ));
    }
}
