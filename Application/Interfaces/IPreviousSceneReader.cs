using Domain.Entities;

namespace Application.Interfaces;

public interface IPreviousSceneReader
{
    Task<SceneSpecification?> GetLatestSceneBySessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<SceneSpecification?> GetSceneByTurnAsync(Guid sessionId, Guid turnId, CancellationToken ct = default);
}
