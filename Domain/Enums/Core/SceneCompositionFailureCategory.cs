namespace Domain.Enums;

/// <summary>
/// Dedicated failure taxonomy for scene composition and generation context resolution.
/// Explicitly decoupled from downstream GPU and provider failure classifications.
/// </summary>
public enum SceneCompositionFailureCategory
{
    None = 0,
    InvalidSceneIntent = 1,
    MissingCharacter = 2,
    MissingVisualContext = 3,
    InvalidSceneRevision = 4,
    ContextResolutionFailure = 5,
    PromptCompositionFailure = 6
}
