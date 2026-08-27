using Application.DTOs;

namespace Application.Interfaces;

/// <summary>
/// Factory that orchestrates database readers to build a fully hydrated SceneCompositionContext.
/// Decouples context assembly from domain composition logic.
/// </summary>
public interface ISceneCompositionContextFactory
{
    Task<SceneCompositionContext> CreateContextAsync(
        Guid characterId,
        Guid? sessionId = null,
        Guid? turnId = null,
        int sceneRevision = 1,
        string? locationContext = null,
        CancellationToken ct = default);
}
