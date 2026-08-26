using Application.DTOs;
using Application.Interfaces;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Services;

/// <summary>
/// Development / Passthrough Identity Quality Evaluator.
/// Implements IIdentityQualityEvaluator for local testing, CI builds, and orchestration validation.
/// Explicitly classified as a baseline passthrough evaluator that validates structural invariants
/// (e.g. non-empty image location) without executing heavy ML / CLIP vision models on CPU/GPU.
/// 
/// In staging/production environments, a dedicated multimodal evaluator (e.g., CLIP / ViT / FaceNet adapter)
/// can be registered in DI to perform real image-to-avatar embedding similarity.
/// </summary>
public sealed class DevelopmentPassThroughIdentityQualityEvaluator : IIdentityQualityEvaluator
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

        // Development baseline pass-through
        return Task.FromResult(IdentityEvaluationResult.Pass(
            faceSimilarity: 0.85f,
            featureScore: 0.90f,
            overallScore: 0.87f
        ));
    }
}

/// <summary>
/// Alias for DevelopmentPassThroughIdentityQualityEvaluator for backwards compatibility.
/// </summary>
public sealed class DefaultIdentityQualityEvaluator : IIdentityQualityEvaluator
{
    private readonly DevelopmentPassThroughIdentityQualityEvaluator _inner = new();

    public Task<IdentityEvaluationResult> EvaluateAsync(
        string imageLocation,
        VisualSnapshot snapshot,
        CancellationToken ct = default)
    {
        return _inner.EvaluateAsync(imageLocation, snapshot, ct);
    }
}
