using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface IVisualStateResolver
{
    Task<(SessionSceneState SceneState, TransientVisualState TransientState, VisualSnapshot Snapshot)> ResolveTurnVisualStateAsync(
        Character character,
        ChatSession session,
        string userMessage,
        string assistantReply,
        CharacterMood currentMood,
        Guid turnId,
        CancellationToken ct = default);
}
