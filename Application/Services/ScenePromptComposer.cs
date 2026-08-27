using System.Text;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;

namespace Application.Services;

public sealed class ScenePromptComposer : IScenePromptComposer
{
    public ScenePrompt ComposePrompt(SceneSpecification scene, VisualContextResolutionResult visualContext)
    {
        ArgumentNullException.ThrowIfNull(scene, nameof(scene));
        ArgumentNullException.ThrowIfNull(visualContext, nameof(visualContext));

        var sb = new StringBuilder();
        var structuredSummary = new StringBuilder();

        // 1. CHARACTER SECTION (Preserve core identity and active appearance)
        var charTraits = new List<string>();
        var appearance = visualContext.CurrentAppearance;
        if (appearance != null)
        {
            if (!string.IsNullOrEmpty(appearance.HairColor)) charTraits.Add($"{appearance.HairColor} hair");
            if (!string.IsNullOrEmpty(appearance.EyeColor)) charTraits.Add($"{appearance.EyeColor} eyes");
            if (!string.IsNullOrEmpty(appearance.SkinTone)) charTraits.Add($"{appearance.SkinTone} skin");
            if (!string.IsNullOrEmpty(appearance.FacialFeatures)) charTraits.Add(appearance.FacialFeatures);
            if (!string.IsNullOrEmpty(appearance.PermanentMarks)) charTraits.Add(appearance.PermanentMarks);
            if (!string.IsNullOrEmpty(appearance.BodyIdentity)) charTraits.Add(appearance.BodyIdentity);
            if (!string.IsNullOrEmpty(appearance.Hairstyle)) charTraits.Add($"hairstyle: {appearance.Hairstyle}");
            if (!string.IsNullOrEmpty(appearance.Makeup)) charTraits.Add($"makeup: {appearance.Makeup}");
        }

        var charSection = charTraits.Count > 0 ? string.Join(", ", charTraits) : "character visual identity";
        sb.Append($"[Character: {charSection}]. ");
        structuredSummary.AppendLine($"CHARACTER: {charSection}");

        // 2. ACTION SECTION
        sb.Append($"[Action: {scene.Action}]. ");
        structuredSummary.AppendLine($"ACTION: {scene.Action}");

        // 3. POSE SECTION
        if (!string.IsNullOrWhiteSpace(scene.Pose))
        {
            sb.Append($"[Pose: {scene.Pose}]. ");
            structuredSummary.AppendLine($"POSE: {scene.Pose}");
        }

        // 4. OUTFIT SECTION
        var outfit = !string.IsNullOrWhiteSpace(scene.OutfitContext)
            ? scene.OutfitContext
            : appearance?.CurrentOutfit;
        if (!string.IsNullOrWhiteSpace(outfit))
        {
            sb.Append($"[Outfit: {outfit}]. ");
            structuredSummary.AppendLine($"OUTFIT: {outfit}");
        }

        // 5. ENVIRONMENT SECTION
        var env = !string.IsNullOrWhiteSpace(scene.Environment) ? scene.Environment : scene.Location;
        sb.Append($"[Environment: {env}]. ");
        structuredSummary.AppendLine($"ENVIRONMENT: {env}");

        // 6. PROPS & OBJECTS SECTION
        if (scene.Objects.Count > 0)
        {
            var propsStr = string.Join(", ", scene.Objects);
            sb.Append($"[Props: {propsStr}]. ");
            structuredSummary.AppendLine($"PROPS: {propsStr}");
        }

        // 7. CAMERA SECTION
        if (!string.IsNullOrWhiteSpace(scene.Camera))
        {
            sb.Append($"[Camera: {scene.Camera}]. ");
            structuredSummary.AppendLine($"CAMERA: {scene.Camera}");
        }

        // 8. LIGHTING SECTION
        if (!string.IsNullOrWhiteSpace(scene.Lighting))
        {
            sb.Append($"[Lighting: {scene.Lighting}]. ");
            structuredSummary.AppendLine($"LIGHTING: {scene.Lighting}");
        }

        // 9. WEATHER & TIME SECTION
        if (!string.IsNullOrWhiteSpace(scene.Weather))
        {
            sb.Append($"[Weather: {scene.Weather}]. ");
            structuredSummary.AppendLine($"WEATHER: {scene.Weather}");
        }

        if (!string.IsNullOrWhiteSpace(scene.TimeOfDay))
        {
            sb.Append($"[Time: {scene.TimeOfDay}]. ");
            structuredSummary.AppendLine($"TIME: {scene.TimeOfDay}");
        }

        // 10. MOOD SECTION
        if (!string.IsNullOrWhiteSpace(scene.Mood))
        {
            sb.Append($"[Mood: {scene.Mood}]. ");
            structuredSummary.AppendLine($"MOOD: {scene.Mood}");
        }

        // 11. CONTINUITY SECTION
        var continuityNote = visualContext.TransitionType switch
        {
            SceneTransitionType.SameScene => "seamless visual continuation of previous scene",
            SceneTransitionType.SameLocation => "same location with shifted camera perspective",
            _ => "new location setting"
        };
        sb.Append($"[Continuity: {continuityNote}].");
        structuredSummary.AppendLine($"CONTINUITY: {continuityNote}");

        var defaultNegatives = "deformed limbs, extra digits, missing fingers, bad anatomy, mutated hands, blurry, low quality, worst quality, watermark, signature";

        return new ScenePrompt(
            PositivePrompt: sb.ToString().Trim(),
            NegativePrompt: defaultNegatives,
            StructuredSummary: structuredSummary.ToString().Trim()
        );
    }
}
