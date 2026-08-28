using Domain.Enums;

namespace Domain.Policies;

/// <summary>
/// Authoritative domain policy determining the continuity relationship between successive scene compositions.
/// Ensures environmental coherence while preventing cross-location visual contamination.
/// </summary>
public static class SceneContinuityPolicy
{
    private static readonly string[] SubLocationSeparators = { " - ", ": ", " / ", " > " };

    public static SceneTransitionType EvaluateTransition(
        string? previousLocation,
        string currentLocation,
        string? previousAction = null,
        string? currentAction = null)
    {
        if (string.IsNullOrWhiteSpace(previousLocation) || string.IsNullOrWhiteSpace(currentLocation))
        {
            return SceneTransitionType.LocationTransition;
        }

        var prevNorm = previousLocation.Trim().ToLowerInvariant();
        var currNorm = currentLocation.Trim().ToLowerInvariant();

        // 1. Exact Location Match
        if (prevNorm == currNorm)
        {
            if (!string.IsNullOrWhiteSpace(previousAction) && !string.IsNullOrWhiteSpace(currentAction))
            {
                var prevAct = previousAction.Trim().ToLowerInvariant();
                var currAct = currentAction.Trim().ToLowerInvariant();

                // If action is continuous within the exact same location -> SameScene
                if (prevAct == currAct || currAct.StartsWith(prevAct) || prevAct.StartsWith(currAct))
                {
                    return SceneTransitionType.SameScene;
                }
            }

            return SceneTransitionType.SameScene;
        }

        // 2. Structured Sub-location Matching (e.g. "Grand Palace - Throne Room" vs "Grand Palace - Courtyard")
        var prevMacro = ExtractMacroLocation(prevNorm);
        var currMacro = ExtractMacroLocation(currNorm);

        if (!string.IsNullOrEmpty(prevMacro) && !string.IsNullOrEmpty(currMacro) && prevMacro == currMacro)
        {
            return SceneTransitionType.SameLocation;
        }

        // 3. Different macro locations -> Strict LocationTransition
        return SceneTransitionType.LocationTransition;
    }

    private static string? ExtractMacroLocation(string location)
    {
        foreach (var sep in SubLocationSeparators)
        {
            var idx = location.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                return location[..idx].Trim();
            }
        }
        return null;
    }
}
