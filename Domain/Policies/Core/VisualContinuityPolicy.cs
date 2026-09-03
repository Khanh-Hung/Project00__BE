using Domain.Entities;
using Domain.Enums;

namespace Domain.Policies;

/// <summary>
/// Authoritative domain policy governing deterministic visual state continuity, scene evolution, authority hierarchy,
/// and transition semantics across conversational turns.
/// </summary>
public static class VisualContinuityPolicy
{
    private static readonly string[] SubLocationSeparators = { " - ", ": ", " / ", " > " };

    /// <summary>
    /// Evaluates transition type between previous scene location and current requested location.
    /// Supports SameScene, SameLocation, LocationTransition, and SceneReentry.
    /// </summary>
    public static SceneTransitionType EvaluateTransition(
        string? previousLocation,
        string currentLocation,
        bool hasHistoricalStateForCurrentLocation = false,
        string? previousAction = null,
        string? currentAction = null)
    {
        if (string.IsNullOrWhiteSpace(currentLocation))
        {
            return SceneTransitionType.LocationTransition;
        }

        var currNorm = currentLocation.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(previousLocation))
        {
            return hasHistoricalStateForCurrentLocation ? SceneTransitionType.SceneReentry : SceneTransitionType.LocationTransition;
        }

        var prevNorm = previousLocation.Trim().ToLowerInvariant();

        // 1. Exact Location Match -> SameScene
        if (prevNorm == currNorm)
        {
            return SceneTransitionType.SameScene;
        }

        // 2. Structured Sub-location Matching (e.g. "Grand Palace - Throne Room" vs "Grand Palace - Courtyard")
        var prevMacro = ExtractMacroLocation(prevNorm);
        var currMacro = ExtractMacroLocation(currNorm);

        if (!string.IsNullOrEmpty(prevMacro) && !string.IsNullOrEmpty(currMacro) && prevMacro == currMacro)
        {
            return SceneTransitionType.SameLocation;
        }

        // 3. Re-entry check into a previously visited distinct scene
        if (hasHistoricalStateForCurrentLocation)
        {
            return SceneTransitionType.SceneReentry;
        }

        // 4. Complete LocationTransition
        return SceneTransitionType.LocationTransition;
    }

    /// <summary>
    /// Resolves character outfit applying strict Authority Hierarchy:
    /// Current Intent > Current Scene State > Recent Valid Visual Memory > Profile Default
    /// Prevents contradictory state merging.
    /// </summary>
    public static (string Outfit, string Source) ResolveOutfit(
        string? intentOutfit,
        string? previousSceneOutfit,
        string? activeMemoryOutfit,
        string? profileDefaultOutfit)
    {
        if (!string.IsNullOrWhiteSpace(intentOutfit))
        {
            return (intentOutfit.Trim(), "CurrentIntent");
        }

        if (!string.IsNullOrWhiteSpace(previousSceneOutfit))
        {
            return (previousSceneOutfit.Trim(), "PreviousSceneState");
        }

        if (!string.IsNullOrWhiteSpace(activeMemoryOutfit))
        {
            return (activeMemoryOutfit.Trim(), "ActiveVisualMemory");
        }

        if (!string.IsNullOrWhiteSpace(profileDefaultOutfit))
        {
            return (profileDefaultOutfit.Trim(), "ProfileDefault");
        }

        return ("Canonical Attire", "SystemDefault");
    }

    /// <summary>
    /// Resolves character hairstyle applying Authority Hierarchy:
    /// Current Intent > Current Scene State > Recent Valid Visual Memory > Profile Default
    /// </summary>
    public static (string? Hairstyle, string Source) ResolveHairstyle(
        string? intentHairstyle,
        string? previousSceneHairstyle,
        string? activeMemoryHairstyle,
        string? profileDefaultHairstyle)
    {
        if (!string.IsNullOrWhiteSpace(intentHairstyle))
        {
            return (intentHairstyle.Trim(), "CurrentIntent");
        }

        if (!string.IsNullOrWhiteSpace(previousSceneHairstyle))
        {
            return (previousSceneHairstyle.Trim(), "PreviousSceneState");
        }

        if (!string.IsNullOrWhiteSpace(activeMemoryHairstyle))
        {
            return (activeMemoryHairstyle.Trim(), "ActiveVisualMemory");
        }

        if (!string.IsNullOrWhiteSpace(profileDefaultHairstyle))
        {
            return (profileDefaultHairstyle.Trim(), "ProfileDefault");
        }

        return (null, "Default");
    }

    /// <summary>
    /// Resolves character pose and action. Action/pose are transient and heavily driven by current intent.
    /// </summary>
    public static (string Pose, string Action, string Source) ResolvePoseAndAction(
        string? intentPose,
        string? intentAction,
        string? previousPose,
        string? previousAction,
        SceneTransitionType transitionType)
    {
        string action = !string.IsNullOrWhiteSpace(intentAction) ? intentAction.Trim() : (previousAction ?? "standing calmly");
        string actionSource = !string.IsNullOrWhiteSpace(intentAction) ? "CurrentIntent" : "PreviousSceneState";

        string pose;
        string poseSource;

        if (!string.IsNullOrWhiteSpace(intentPose))
        {
            pose = intentPose.Trim();
            poseSource = "CurrentIntent";
        }
        else
        {
            var actionLower = action.ToLowerInvariant();
            if (actionLower.Contains("read") || actionLower.Contains("sit") || actionLower.Contains("rest") || actionLower.Contains("eat") || actionLower.Contains("drink")
                || actionLower.Contains("đọc") || actionLower.Contains("ngồi") || actionLower.Contains("uống") || actionLower.Contains("ăn"))
            {
                pose = "seated naturally";
                poseSource = "ActionInference";
            }
            else if (actionLower.Contains("sleep") || actionLower.Contains("lie") || actionLower.Contains("lay") || actionLower.Contains("nằm") || actionLower.Contains("ngủ"))
            {
                pose = "lying down relaxed";
                poseSource = "ActionInference";
            }
            else if (actionLower.Contains("walk") || actionLower.Contains("run") || actionLower.Contains("stride") || actionLower.Contains("bước") || actionLower.Contains("chạy") || actionLower.Contains("đi"))
            {
                pose = "mid-stride dynamic movement";
                poseSource = "ActionInference";
            }
            else if (transitionType == SceneTransitionType.SameScene && !string.IsNullOrWhiteSpace(previousPose))
            {
                pose = previousPose;
                poseSource = "PreviousSceneState";
            }
            else
            {
                pose = "standing naturally";
                poseSource = "Default";
            }
        }

        return (pose, action, $"{actionSource}+{poseSource}");
    }

    /// <summary>
    /// Resolves weather, lighting, and time of day based on transition semantics.
    /// SameScene preserves weather and lighting; LocationTransition recalculates for the new environment.
    /// </summary>
    public static (string Weather, string TimeOfDay, string Lighting, string Source) ResolveEnvironment(
        string? intentWeather,
        string? intentTimeOfDay,
        string? intentLighting,
        SceneVisualState? previousState,
        SceneTransitionType transitionType,
        bool isOutdoors)
    {
        string weather;
        string weatherSource;
        if (!string.IsNullOrWhiteSpace(intentWeather))
        {
            weather = intentWeather.Trim();
            weatherSource = "CurrentIntent";
        }
        else if (transitionType == SceneTransitionType.SameScene && previousState != null && !string.IsNullOrWhiteSpace(previousState.Weather))
        {
            weather = previousState.Weather;
            weatherSource = "PreviousSceneState";
        }
        else
        {
            weather = "Clear";
            weatherSource = "Default";
        }

        string timeOfDay;
        string timeSource;
        if (!string.IsNullOrWhiteSpace(intentTimeOfDay))
        {
            timeOfDay = intentTimeOfDay.Trim();
            timeSource = "CurrentIntent";
        }
        else if ((transitionType == SceneTransitionType.SameScene || transitionType == SceneTransitionType.SameLocation) && previousState != null && !string.IsNullOrWhiteSpace(previousState.TimeOfDay))
        {
            timeOfDay = previousState.TimeOfDay;
            timeSource = "PreviousSceneState";
        }
        else
        {
            timeOfDay = "Daytime";
            timeSource = "Default";
        }

        string lighting;
        string lightingSource;
        if (!string.IsNullOrWhiteSpace(intentLighting))
        {
            lighting = intentLighting.Trim();
            lightingSource = "CurrentIntent";
        }
        else if (transitionType == SceneTransitionType.SameScene && previousState != null && !string.IsNullOrWhiteSpace(previousState.Lighting))
        {
            lighting = previousState.Lighting;
            lightingSource = "PreviousSceneState";
        }
        else
        {
            var timeLower = timeOfDay.ToLowerInvariant();
            var isNight = timeLower.Contains("night") || timeLower.Contains("midnight") || timeLower.Contains("đêm");
            var isSunset = timeLower.Contains("sunset") || timeLower.Contains("golden") || timeLower.Contains("dusk") || timeLower.Contains("hoàng hôn");

            if (isOutdoors)
            {
                if (isNight) lighting = "soft moonlight with ambient night shadows";
                else if (isSunset) lighting = "warm golden hour sunlight with elongated soft shadows";
                else lighting = "natural diffused daylight";
            }
            else
            {
                if (isNight) lighting = "warm interior candle and lantern glow";
                else lighting = "ambient interior lighting mixed with soft window daylight";
            }
            lightingSource = "LocationDefault";
        }

        return (weather, timeOfDay, lighting, $"{weatherSource}|{timeSource}|{lightingSource}");
    }

    /// <summary>
    /// Resolves props and persistent world mutations.
    /// SameScene and SceneReentry carry persistent world mutations (e.g. Glass: BrokenOnFloor).
    /// LocationTransition resets location-specific furniture/props while keeping character active props.
    /// </summary>
    public static (IReadOnlyList<string> Props, IReadOnlyDictionary<string, string> PersistentChanges, string Source) ResolvePropsAndWorldMutations(
        IEnumerable<string>? intentProps,
        SceneVisualState? previousState,
        SceneVisualState? reenteredHistoricalState,
        SceneTransitionType transitionType)
    {
        var propsList = new List<string>(intentProps ?? Array.Empty<string>());
        var mutations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string source;

        if (transitionType == SceneTransitionType.SameScene && previousState != null)
        {
            foreach (var prop in previousState.Props)
            {
                if (!propsList.Contains(prop, StringComparer.OrdinalIgnoreCase))
                {
                    propsList.Add(prop);
                }
            }

            foreach (var kvp in previousState.PersistentChanges)
            {
                mutations[kvp.Key] = kvp.Value;
            }
            source = "PreviousSceneState+Intent";
        }
        else if (transitionType == SceneTransitionType.SceneReentry && reenteredHistoricalState != null)
        {
            foreach (var prop in reenteredHistoricalState.Props)
            {
                if (!propsList.Contains(prop, StringComparer.OrdinalIgnoreCase))
                {
                    propsList.Add(prop);
                }
            }

            foreach (var kvp in reenteredHistoricalState.PersistentChanges)
            {
                mutations[kvp.Key] = kvp.Value;
            }
            source = "SceneReentryRestored+Intent";
        }
        else
        {
            source = "CurrentIntent";
        }

        return (propsList, mutations, source);
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
