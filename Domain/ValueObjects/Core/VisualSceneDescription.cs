using System.Collections.Immutable;

namespace Domain.ValueObjects;

/// <summary>
/// Structured cinematic composition and narrative scene description for a conversation turn.
/// Frozen inside VisualSnapshot to guarantee deterministic downstream prompt compilation and image synthesis.
/// Deep-immutable record with zero mutable collections.
/// </summary>
public sealed record VisualSceneDescription
{
    public string? ShotType { get; init; }
    public string? CameraAngle { get; init; }
    public string? SubjectPlacement { get; init; }
    public string? DetailedAction { get; init; }
    public string? DetailedEnvironment { get; init; }
    public string? LightingStyle { get; init; }
    public string? Atmosphere { get; init; }
    public ImmutableArray<string> EnglishPromptTags { get; init; }

    [System.Text.Json.Serialization.JsonConstructor]
    public VisualSceneDescription(
        string? shotType,
        string? cameraAngle,
        string? subjectPlacement,
        string? detailedAction,
        string? detailedEnvironment,
        string? lightingStyle,
        string? atmosphere,
        ImmutableArray<string> englishPromptTags)
    {
        ShotType = shotType;
        CameraAngle = cameraAngle;
        SubjectPlacement = subjectPlacement;
        DetailedAction = detailedAction;
        DetailedEnvironment = detailedEnvironment;
        LightingStyle = lightingStyle;
        Atmosphere = atmosphere;
        EnglishPromptTags = englishPromptTags.IsDefault ? ImmutableArray<string>.Empty : englishPromptTags;
    }

    public VisualSceneDescription(
        string? shotType = null,
        string? cameraAngle = null,
        string? subjectPlacement = null,
        string? detailedAction = null,
        string? detailedEnvironment = null,
        string? lightingStyle = null,
        string? atmosphere = null,
        IEnumerable<string>? englishPromptTags = null)
        : this(
            shotType,
            cameraAngle,
            subjectPlacement,
            detailedAction,
            detailedEnvironment,
            lightingStyle,
            atmosphere,
            englishPromptTags != null
                ? englishPromptTags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToImmutableArray()
                : ImmutableArray<string>.Empty)
    {
    }

    /// <summary>
    /// Performs semantic validation and factual anchoring against the character identity and projected persistent scene state for Turn N.
    /// Strips conflicting identity tags (hair/eyes) and unmentioned hallucinated environments/lighting (e.g. sunset, beach).
    /// </summary>
    public static VisualSceneDescription Sanitize(
        VisualSceneDescription? rawDesc,
        CharacterVisualIdentity? identity,
        SessionSceneState projectedState,
        string userMessage,
        string assistantMessage)
    {
        if (rawDesc == null)
            return new VisualSceneDescription();

        var combinedDialogue = $"{userMessage} {assistantMessage}".ToLowerInvariant();
        var sanitizedTags = new List<string>();

        // 1. Identity Protection: Hair color conflicts
        var charHair = identity?.Hair?.ToLowerInvariant() ?? string.Empty;
        var knownHairColors = new[] { "blonde hair", "silver hair", "white hair", "black hair", "brown hair", "pink hair", "blue hair", "red hair", "green hair", "purple hair" };

        // 2. Identity Protection: Eye color conflicts
        var charEyes = identity?.Eyes?.ToLowerInvariant() ?? string.Empty;
        var knownEyeColors = new[] { "blue eyes", "green eyes", "emerald eyes", "red eyes", "brown eyes", "amber eyes", "purple eyes", "golden eyes" };

        // 3. Environmental Grounding: Check if projected location is indoor vs outdoor setting
        var location = projectedState.CurrentLocation?.ToLowerInvariant() ?? string.Empty;
        var isExplicitlyOutdoorLocation = location.Contains("courtyard") || location.Contains("garden") || location.Contains("balcony")
            || location.Contains("street") || location.Contains("plaza") || location.Contains("park") || location.Contains("beach")
            || location.Contains("forest") || location.Contains("mountain") || location.Contains("terrace") || location.Contains("rooftop")
            || location.Contains("sân") || location.Contains("vườn") || location.Contains("ngoài");

        var isIndoors = !isExplicitlyOutdoorLocation && (
            location.Contains("room") || location.Contains("bedroom") || location.Contains("library") 
            || location.Contains("workshop") || location.Contains("temple") || location.Contains("sanctuary") 
            || location.Contains("castle") || location.Contains("hall") || location.Contains("kitchen") 
            || location.Contains("phòng") || location.Contains("đại điện") || location.Contains("bay") 
            || location.Contains("cabin") || location.Contains("bridge") || location.Contains("laboratory")
        );

        var hasOutdoorDialogue = combinedDialogue.Contains("outdoor") || combinedDialogue.Contains("outside") 
                              || combinedDialogue.Contains("garden") || combinedDialogue.Contains("beach") 
                              || combinedDialogue.Contains("ocean") || combinedDialogue.Contains("sea") 
                              || combinedDialogue.Contains("forest") || combinedDialogue.Contains("mountain") 
                              || combinedDialogue.Contains("bãi biển") || combinedDialogue.Contains("khu vườn") 
                              || combinedDialogue.Contains("rừng") || combinedDialogue.Contains("ngoài trời") 
                              || combinedDialogue.Contains("ban công") || combinedDialogue.Contains("balcony")
                              || combinedDialogue.Contains("courtyard") || combinedDialogue.Contains("sân");

        var forbiddenOutdoorTags = new[] { "beach", "ocean", "seashore", "deep forest", "in the forest", "jungle" };

        // 4. TimeOfDay Grounding
        var timeOfDay = projectedState.CurrentTimeOfDay?.ToLowerInvariant() ?? string.Empty;
        var isNight = timeOfDay.Contains("night") || timeOfDay.Contains("midnight") || timeOfDay.Contains("đêm");
        var hasSunsetDialogue = combinedDialogue.Contains("sunset") || combinedDialogue.Contains("sunrise") 
                             || combinedDialogue.Contains("hoàng hôn") || combinedDialogue.Contains("bình minh");
        var forbiddenDayTags = new[] { "sunset", "golden hour", "sunrise", "bright sunlight", "daylight" };

        foreach (var tag in rawDesc.EnglishPromptTags)
        {
            var lowerTag = tag.ToLowerInvariant().Trim();

            // Check hair conflict
            if (!string.IsNullOrEmpty(charHair))
            {
                var isConflictingHair = false;
                foreach (var knownHair in knownHairColors)
                {
                    if (lowerTag.Contains(knownHair) && !charHair.Contains(knownHair.Replace(" hair", "")))
                    {
                        isConflictingHair = true;
                        break;
                    }
                }
                if (isConflictingHair) continue;
            }

            // Check eye conflict
            if (!string.IsNullOrEmpty(charEyes))
            {
                var isConflictingEye = false;
                foreach (var knownEye in knownEyeColors)
                {
                    if (lowerTag.Contains(knownEye) && !charEyes.Contains(knownEye.Replace(" eyes", "")))
                    {
                        isConflictingEye = true;
                        break;
                    }
                }
                if (isConflictingEye) continue;
            }

            // Check indoor vs outdoor conflict
            if (isIndoors && !hasOutdoorDialogue && forbiddenOutdoorTags.Any(f => lowerTag.Contains(f)))
            {
                continue;
            }

            // Check night vs sunset conflict
            if (isNight && !hasSunsetDialogue && forbiddenDayTags.Any(f => lowerTag.Contains(f)))
            {
                continue;
            }

            sanitizedTags.Add(tag);
        }

        // Sanitize LightingStyle if sunset hallucinated at night indoors
        var sanitizedLighting = rawDesc.LightingStyle;
        if (isNight && !hasSunsetDialogue && !string.IsNullOrEmpty(sanitizedLighting) && forbiddenDayTags.Any(f => sanitizedLighting.ToLowerInvariant().Contains(f)))
        {
            sanitizedLighting = isIndoors ? "warm indoor ambient light, soft room glow" : "night ambient light, soft moonlight";
        }

        // Sanitize DetailedEnvironment if outdoor hallucinated while indoors
        var sanitizedEnvironment = rawDesc.DetailedEnvironment;
        if (isIndoors && !hasOutdoorDialogue && !string.IsNullOrEmpty(sanitizedEnvironment) && forbiddenOutdoorTags.Any(f => sanitizedEnvironment.ToLowerInvariant().Contains(f)))
        {
            sanitizedEnvironment = $"indoor {projectedState.CurrentLocation} setting";
        }

        return new VisualSceneDescription(
            shotType: rawDesc.ShotType,
            cameraAngle: rawDesc.CameraAngle,
            subjectPlacement: rawDesc.SubjectPlacement,
            detailedAction: rawDesc.DetailedAction,
            detailedEnvironment: sanitizedEnvironment,
            lightingStyle: sanitizedLighting,
            atmosphere: rawDesc.Atmosphere,
            englishPromptTags: sanitizedTags
        );
    }
}
