using Application.DTOs;
using Domain.Entities;

namespace Application.Interfaces;

public interface IVisualContextResolver
{
    Task<VisualContextResolutionResult> ResolveVisualContextAsync(
        Guid characterId,
        SceneSpecification scene,
        SceneCompositionContext context,
        CancellationToken ct = default);
}
