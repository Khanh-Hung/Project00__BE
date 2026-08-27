using Domain.Enums;

namespace Domain.Policies;

/// <summary>
/// Domain policy determining the continuity relationship between successive scene compositions.
/// Ensures environmental coherence while preventing cross-location visual contamination.
/// </summary>
public static class SceneContinuityPolicy
{
    public static SceneTransitionType EvaluateTransition(
        string? previousLocation,
        string currentLocation,
        string? previousAction = null,
        string? currentAction = null)
    {
        if (string.IsNullOrWhiteSpace(previousLocation))
        {
            return SceneTransitionType.LocationTransition;
        }

        var prevNorm = previousLocation.Trim().ToLowerInvariant();
        var currNorm = currentLocation.Trim().ToLowerInvariant();

        if (prevNorm == currNorm)
        {
            // Exact same location: check if action is a continuous progression or radical reset
            if (!string.IsNullOrWhiteSpace(previousAction) && !string.IsNullOrWhiteSpace(currentAction))
            {
                var prevAct = previousAction.Trim().ToLowerInvariant();
                var currAct = currentAction.Trim().ToLowerInvariant();

                if (prevAct == currAct || currAct.Contains(prevAct) || prevAct.Contains(currAct))
                {
                    return SceneTransitionType.SameScene;
                }
            }

            return SceneTransitionType.SameScene;
        }

        // Check if both are sub-locations of the same macro environment (e.g. "Gothic Library - First Floor" vs "Gothic Library - Balcony")
        if (prevNorm.Contains(currNorm) || currNorm.Contains(prevNorm))
        {
            return SceneTransitionType.SameLocation;
        }

        return SceneTransitionType.LocationTransition;
    }
}
