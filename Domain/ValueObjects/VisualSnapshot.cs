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
    GenerationProfile GenerationProfile,
    string? IdentityReferenceUrl = null,
    string? PreviousSceneImageUrl = null,
    int? PredecessorSceneRevision = null,
    Guid? PredecessorSceneImageId = null,
    string? NegativeConstraints = null,
    DateTime? CreatedAt = null,
    VisualSceneDescription? SceneDescription = null
)
{
    /// <summary>
    /// Factory helper to build an immutable snapshot with strict canonical reference and generation profile.
    /// </summary>
    public static VisualSnapshot Create(
        Guid turnId,
        Guid sessionId,
        Guid characterId,
        int sceneRevision,
        CharacterVisualIdentity? visualIdentity,
        SessionSceneState sceneState,
        TransientVisualState? transientState,
        GenerationProfile generationProfile,
        string? previousSceneImageUrl = null,
        int? predecessorSceneRevision = null,
        Guid? predecessorSceneImageId = null,
        string? negativeConstraints = null,
        string? fallbackReferenceUrl = null,
        VisualSceneDescription? sceneDescription = null)
    {
        ArgumentNullException.ThrowIfNull(generationProfile, nameof(generationProfile));

        // Strict resolution hierarchy: CanonicalReferenceUrl (tight face crop) -> Character AvatarUrl -> FullBodyUrl
        var resolvedIdentityRef = !string.IsNullOrWhiteSpace(visualIdentity?.CanonicalReferenceUrl)
            ? visualIdentity.CanonicalReferenceUrl
            : (!string.IsNullOrWhiteSpace(fallbackReferenceUrl)
                ? fallbackReferenceUrl
                : (!string.IsNullOrWhiteSpace(visualIdentity?.FullBodyUrl)
                    ? visualIdentity.FullBodyUrl
                    : null));

        var defaultNegatives = negativeConstraints 
            ?? "deformed horns, extra horns, asymmetrical malformed horns, bad anatomy, bad hands, missing fingers, extra digits, cropped, signature, watermark, blurry, low quality, worst quality";

        return new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: sceneRevision,
            VisualIdentity: visualIdentity,
            SceneState: sceneState,
            TransientState: transientState,
            GenerationProfile: generationProfile,
            IdentityReferenceUrl: resolvedIdentityRef,
            PreviousSceneImageUrl: previousSceneImageUrl,
            PredecessorSceneRevision: predecessorSceneRevision ?? (sceneRevision > 1 ? sceneRevision - 1 : null),
            PredecessorSceneImageId: predecessorSceneImageId,
            NegativeConstraints: defaultNegatives,
            CreatedAt: DateTime.UtcNow,
            SceneDescription: sceneDescription
        );
    }
}
