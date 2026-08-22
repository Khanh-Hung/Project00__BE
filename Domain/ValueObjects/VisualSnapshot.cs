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
    GenerationProfile? GenerationProfile = null,
    string? IdentityReferenceUrl = null,
    string? PreviousSceneImageUrl = null,
    string? NegativeConstraints = null,
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
        string? previousSceneImageUrl = null,
        GenerationProfile? generationProfile = null,
        string? negativeConstraints = null)
    {
        var resolvedIdentityRef = visualIdentity?.CanonicalReferenceUrl
            ?? visualIdentity?.FullBodyUrl
            ?? (!string.IsNullOrWhiteSpace(characterAvatarUrl) ? characterAvatarUrl : null);

        var profile = generationProfile ?? GenerationProfile.CreateDefault();

        var defaultNegatives = negativeConstraints 
            ?? "black clothing, red clothing, black-red outfit, dark clothing, crimson outfit, black dress, red dress, dark bodysuit, black bodysuit, no horns, missing horns, human ears only, deformed horns, bad anatomy, bad hands, missing fingers, extra digits, cropped, signature, watermark, blurry, low quality, worst quality";

        return new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: sceneRevision,
            VisualIdentity: visualIdentity,
            SceneState: sceneState,
            TransientState: transientState,
            GenerationProfile: profile,
            IdentityReferenceUrl: resolvedIdentityRef,
            PreviousSceneImageUrl: previousSceneImageUrl,
            NegativeConstraints: defaultNegatives,
            CreatedAt: DateTime.UtcNow
        );
    }
}
