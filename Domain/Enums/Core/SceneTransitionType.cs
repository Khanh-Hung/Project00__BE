namespace Domain.Enums;

/// <summary>
/// Classified scene transition type between successive conversation turns.
/// </summary>
public enum SceneTransitionType
{
    /// <summary>
    /// Continuation within the exact same visual scene and environment (preserves backdrop, lighting, weather).
    /// </summary>
    SameScene = 0,

    /// <summary>
    /// Same macro location with camera or perspective repositioning (preserves architecture, major props).
    /// </summary>
    SameLocation = 1,

    /// <summary>
    /// Complete spatial transition to a new location (resets environment while preserving character identity and temporal progression).
    /// </summary>
    LocationTransition = 2,

    /// <summary>
    /// Re-entry into a previously visited location (restores latest valid location-specific state and persistent world changes).
    /// </summary>
    SceneReentry = 3
}
