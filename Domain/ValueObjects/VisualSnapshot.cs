namespace Domain.ValueObjects;

/// <summary>
/// Deep-immutable Visual Snapshot captured at the atomic commit boundary of Turn N.
/// Snapshot contains pure immutable value objects with no mutable collections.
/// Serves as the single immutable source of truth for downstream image synthesis and outbox workers.
/// </summary>
public sealed record VisualSnapshot(
    Guid TurnId,
    Guid SessionId,
    Guid CharacterId,
    int SceneRevision,
    CharacterVisualIdentity? VisualIdentity,
    SessionSceneState SceneState,
    TransientVisualState? TransientState,
    string? IdentityReferenceUrl = null,
    string? PreviousSceneImageUrl = null,
    DateTime? CreatedAt = null
)
{
    /// <summary>
    /// Factory helper to build a snapshot with strict canonical reference resolution order:
    /// CanonicalReferenceUrl -> FullBodyUrl -> AvatarUrl
    /// </summary>
    public static VisualSnapshot Create(
        Guid turnId,
        Guid sessionId,
        Guid characterId,
        int sceneRevision,
        CharacterVisualIdentity? visualIdentity,
        string? characterAvatarUrl,
        SessionSceneState sceneState,
        TransientVisualState? transientState,
        string? previousSceneImageUrl = null)
    {
        var resolvedIdentityRef = visualIdentity?.CanonicalReferenceUrl
            ?? visualIdentity?.FullBodyUrl
            ?? (!string.IsNullOrWhiteSpace(characterAvatarUrl) ? characterAvatarUrl : null);

        return new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: sceneRevision,
            VisualIdentity: visualIdentity,
            SceneState: sceneState,
            TransientState: transientState,
            IdentityReferenceUrl: resolvedIdentityRef,
            PreviousSceneImageUrl: previousSceneImageUrl,
            CreatedAt: DateTime.UtcNow
        );
    }
}
