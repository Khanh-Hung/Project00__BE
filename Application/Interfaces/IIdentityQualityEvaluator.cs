using Application.DTOs;
using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Contract for evaluating a generated image against canonical character identity invariants and turn snapshots.
/// Implementations reside in Infrastructure (e.g. CLIP / ViT / FaceNet adapters).
/// </summary>
public interface IIdentityQualityEvaluator
{
    /// <summary>
    /// Evaluates the visual identity quality of a generated image against canonical references and invariants.
    /// </summary>
    Task<IdentityEvaluationResult> EvaluateAsync(
        string imageLocation,
        VisualSnapshot snapshot,
        CancellationToken ct = default);
}
