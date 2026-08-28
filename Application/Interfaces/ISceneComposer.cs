using Application.DTOs;
using Domain.Entities;

namespace Application.Interfaces;

public interface ISceneComposer
{
    Task<SceneSpecification> ComposeAsync(
        SceneIntent intent,
        SceneCompositionContext context,
        SceneVisualState? visualState = null,
        CancellationToken ct = default);
}
