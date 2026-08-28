using Domain.Entities;

namespace Application.Interfaces;

/// <summary>
/// Authoritative persistence and retrieval contract for SceneVisualState aggregates.
/// Enforces optimistic concurrency fencing and unique current-state invariants per (SessionId, SceneKey).
/// </summary>
public interface ISceneVisualStateReader
{
    Task<SceneVisualState?> GetLatestBySessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<SceneVisualState?> GetLatestBySessionAndSceneKeyAsync(Guid sessionId, string sceneKey, CancellationToken ct = default);
    Task SaveStateAsync(SceneVisualState state, uint expectedVersion = 0, CancellationToken ct = default);
}
