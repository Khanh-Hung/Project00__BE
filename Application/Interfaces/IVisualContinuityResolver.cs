using Application.DTOs;

namespace Application.Interfaces;

/// <summary>
/// Authoritative domain service resolving deterministic scene evolution, character visual state continuity,
/// world mutations, transition semantics, and authority hierarchy between successive conversational turns.
/// </summary>
public interface IVisualContinuityResolver
{
    Task<VisualContinuityResult> ResolveAsync(
        VisualContinuityRequest request,
        CancellationToken ct = default);
}
