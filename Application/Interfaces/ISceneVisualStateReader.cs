using Domain.Entities;

namespace Application.Interfaces;

/// <summary>
/// Reader interface for querying historical and active SceneVisualState records.
/// </summary>
public interface ISceneVisualStateReader
{
    Task<SceneVisualState?> GetLatestBySessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<SceneVisualState?> GetLatestBySessionAndSceneKeyAsync(Guid sessionId, string sceneKey, CancellationToken ct = default);
    Task SaveStateAsync(SceneVisualState state, CancellationToken ct = default);
}
