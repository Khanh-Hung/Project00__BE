using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface ISceneStateTrackerService
{
    Task<SessionSceneState> TrackAndExtractStateAsync(
        Character character,
        SessionSceneState? currentState,
        string userMessage,
        string assistantMessage,
        CancellationToken ct = default);
}
