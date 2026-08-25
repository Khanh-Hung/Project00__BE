using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Services;

public sealed class VisualPromptCompiler : IVisualPromptCompiler
{
    public string CompileAvatarPrompt(Character character)
    {
        var identity = character.VisualIdentity;
        var traits = new List<string>();

        if (identity != null)
        {
            if (!string.IsNullOrWhiteSpace(identity.AgeAppearance)) traits.Add(identity.AgeAppearance);
            if (!string.IsNullOrWhiteSpace(identity.Hair)) traits.Add(identity.Hair);
            if (!string.IsNullOrWhiteSpace(identity.Eyes)) traits.Add(identity.Eyes);
            if (!string.IsNullOrWhiteSpace(identity.Face)) traits.Add(identity.Face);
            if (!string.IsNullOrWhiteSpace(identity.Skin)) traits.Add(identity.Skin);
            if (!string.IsNullOrWhiteSpace(identity.Body)) traits.Add(identity.Body);
            if (!string.IsNullOrWhiteSpace(identity.ClothingStyle)) traits.Add(identity.ClothingStyle);
            if (!string.IsNullOrWhiteSpace(identity.Accessories)) traits.Add(identity.Accessories);
            if (!string.IsNullOrWhiteSpace(identity.VisualTraits)) traits.Add(identity.VisualTraits);
        }
        else
        {
            traits.Add($"1girl, {character.Name}");
            if (!string.IsNullOrWhiteSpace(character.Category)) traits.Add(character.Category);
            if (!string.IsNullOrWhiteSpace(character.Title)) traits.Add(character.Title);
        }

        var identityTags = string.Join(", ", traits.Where(t => !string.IsNullOrWhiteSpace(t)));
        return $"solo, close-up portrait, {identityTags}, looking at viewer, gentle smile, atmospheric lighting, detailed background";
    }

    public string CompileScenePrompt(Character character, SceneContext scene, CharacterRelationship? relationship = null)
    {
        var identity = character.VisualIdentity;
        var characterTags = new List<string>();

        // 1. Immutable Visual Foundation (Hierarchy: Identity > Scene > Mood > Relationship)
        if (identity != null)
        {
            if (!string.IsNullOrWhiteSpace(identity.AgeAppearance)) characterTags.Add(identity.AgeAppearance);
            if (!string.IsNullOrWhiteSpace(identity.Hair)) characterTags.Add(identity.Hair);
            if (!string.IsNullOrWhiteSpace(identity.Eyes)) characterTags.Add(identity.Eyes);
            if (!string.IsNullOrWhiteSpace(identity.Face)) characterTags.Add(identity.Face);
            if (!string.IsNullOrWhiteSpace(identity.Skin)) characterTags.Add(identity.Skin);
            if (!string.IsNullOrWhiteSpace(identity.Body)) characterTags.Add(identity.Body);
            
            // Default clothing if scene doesn't specify specific outfit
            if (string.IsNullOrWhiteSpace(scene.Outfit) && !string.IsNullOrWhiteSpace(identity.ClothingStyle))
            {
                characterTags.Add(identity.ClothingStyle);
            }
            if (!string.IsNullOrWhiteSpace(identity.Accessories)) characterTags.Add(identity.Accessories);
            if (!string.IsNullOrWhiteSpace(identity.VisualTraits)) characterTags.Add(identity.VisualTraits);
        }
        else
        {
            characterTags.Add($"1girl, {character.Name}");
            if (!string.IsNullOrWhiteSpace(character.Title)) characterTags.Add(character.Title);
        }

        // 2. Dynamic Emotional Expression from Scene & Relationship Mood
        var expressionTags = new List<string>();
        if (!string.IsNullOrWhiteSpace(scene.Expression))
        {
            expressionTags.Add(scene.Expression);
        }
        else if (relationship != null)
        {
            var moodExpression = MapMoodToVisualExpression(relationship.CurrentMood, relationship.MoodIntensity);
            if (!string.IsNullOrWhiteSpace(moodExpression)) expressionTags.Add(moodExpression);
        }
        else
        {
            expressionTags.Add("gentle expression, soft gaze");
        }

        // 3. Dynamic Scene Context & Interaction
        var sceneTags = new List<string>();
        if (!string.IsNullOrWhiteSpace(scene.Outfit)) sceneTags.Add($"wearing {scene.Outfit}");
        if (!string.IsNullOrWhiteSpace(scene.Pose)) sceneTags.Add(scene.Pose);
        if (!string.IsNullOrWhiteSpace(scene.Action)) sceneTags.Add(scene.Action);
        if (!string.IsNullOrWhiteSpace(scene.Location)) sceneTags.Add($"in {scene.Location}");
        if (!string.IsNullOrWhiteSpace(scene.TimeOfDay)) sceneTags.Add(scene.TimeOfDay);

        // 4. Intimacy Aura from Relationship Affection (Affects interaction atmosphere only, never physical identity)
        if (relationship != null && relationship.AffectionScore >= 70)
        {
            expressionTags.Add("loving eye contact, intimate atmosphere, subtle blush");
        }

        var identityPart = string.Join(", ", characterTags.Where(t => !string.IsNullOrWhiteSpace(t)));
        var expressionPart = string.Join(", ", expressionTags.Where(t => !string.IsNullOrWhiteSpace(t)));
        var scenePart = string.Join(", ", sceneTags.Where(t => !string.IsNullOrWhiteSpace(t)));

        var parts = new List<string> { identityPart, expressionPart, scenePart, "cinematic composition, dramatic lighting, detailed background" };
        return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>
    /// Deterministic prompt compilation purely from the frozen VisualSnapshot of Turn N.
    /// Single Source of Truth for Outbox Workers.
    /// </summary>
    public string CompileScenePrompt(VisualSnapshot snapshot)
    {
        if (snapshot == null) return string.Empty;

        var characterTags = new List<string> { "masterpiece", "best quality", "solo" };
        var identity = snapshot.VisualIdentity;
        if (identity != null)
        {
            if (!string.IsNullOrWhiteSpace(identity.Gender))
                characterTags.Add(identity.Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "1girl" : "1boy");
            else
                characterTags.Add("1girl");

            if (!string.IsNullOrWhiteSpace(identity.AgeAppearance)) characterTags.Add(identity.AgeAppearance);
            if (!string.IsNullOrWhiteSpace(identity.Hair)) characterTags.Add(identity.Hair);
            if (!string.IsNullOrWhiteSpace(identity.Eyes)) characterTags.Add(identity.Eyes);
            if (!string.IsNullOrWhiteSpace(identity.Face)) characterTags.Add(identity.Face);
            if (!string.IsNullOrWhiteSpace(identity.Skin)) characterTags.Add(identity.Skin);
            if (!string.IsNullOrWhiteSpace(identity.Body)) characterTags.Add(identity.Body);
            if (!string.IsNullOrWhiteSpace(identity.Accessories)) characterTags.Add(identity.Accessories);
            if (!string.IsNullOrWhiteSpace(identity.VisualTraits)) characterTags.Add(identity.VisualTraits);
        }
        else
        {
            characterTags.Add("1girl");
        }

        var sceneDesc = snapshot.SceneDescription;
        var sceneTags = new List<string>();

        if (sceneDesc != null && sceneDesc.EnglishPromptTags != null && sceneDesc.EnglishPromptTags.Count > 0)
        {
            sceneTags.AddRange(sceneDesc.EnglishPromptTags.Where(t => !string.IsNullOrWhiteSpace(t)));
        }
        else if (sceneDesc != null)
        {
            if (!string.IsNullOrWhiteSpace(sceneDesc.ShotType)) sceneTags.Add(sceneDesc.ShotType);
            if (!string.IsNullOrWhiteSpace(sceneDesc.CameraAngle)) sceneTags.Add(sceneDesc.CameraAngle);
            if (!string.IsNullOrWhiteSpace(sceneDesc.SubjectPlacement)) sceneTags.Add(sceneDesc.SubjectPlacement);
            if (!string.IsNullOrWhiteSpace(sceneDesc.DetailedAction)) sceneTags.Add(sceneDesc.DetailedAction);
            if (!string.IsNullOrWhiteSpace(sceneDesc.DetailedEnvironment)) sceneTags.Add(sceneDesc.DetailedEnvironment);
            if (!string.IsNullOrWhiteSpace(sceneDesc.LightingStyle)) sceneTags.Add(sceneDesc.LightingStyle);
            if (!string.IsNullOrWhiteSpace(sceneDesc.Atmosphere)) sceneTags.Add(sceneDesc.Atmosphere);
        }
        else
        {
            var expressionTags = new List<string>();
            var transient = snapshot.TransientState;
            if (transient != null)
            {
                if (!string.IsNullOrWhiteSpace(transient.Expression)) expressionTags.Add(transient.Expression);
                if (!string.IsNullOrWhiteSpace(transient.Gaze)) expressionTags.Add(transient.Gaze);
                if (!string.IsNullOrWhiteSpace(transient.Gesture)) expressionTags.Add(transient.Gesture);
            }
            if (expressionTags.Count == 0)
            {
                expressionTags.Add("gentle expression, soft gaze");
            }

            var scene = snapshot.SceneState;
            if (scene != null)
            {
                if (!string.IsNullOrWhiteSpace(scene.CurrentOutfit))
                    sceneTags.Add($"wearing {scene.CurrentOutfit}");
                else if (!string.IsNullOrWhiteSpace(identity?.ClothingStyle))
                    sceneTags.Add($"wearing {identity.ClothingStyle}");

                if (transient != null)
                {
                    if (!string.IsNullOrWhiteSpace(transient.Pose)) sceneTags.Add(transient.Pose);
                    if (!string.IsNullOrWhiteSpace(transient.Action)) sceneTags.Add(transient.Action);
                    if (!string.IsNullOrWhiteSpace(transient.Interaction)) sceneTags.Add(transient.Interaction);
                }

                if (!string.IsNullOrWhiteSpace(scene.CurrentPosition) && !string.IsNullOrWhiteSpace(scene.CurrentLocation))
                    sceneTags.Add($"at {scene.CurrentPosition}, in {scene.CurrentLocation}");
                else if (!string.IsNullOrWhiteSpace(scene.CurrentPosition))
                    sceneTags.Add($"at {scene.CurrentPosition}");
                else if (!string.IsNullOrWhiteSpace(scene.CurrentLocation))
                    sceneTags.Add($"in {scene.CurrentLocation}");

                if (!string.IsNullOrWhiteSpace(scene.CurrentTimeOfDay)) sceneTags.Add(scene.CurrentTimeOfDay);
                if (!string.IsNullOrWhiteSpace(scene.HeldItems)) sceneTags.Add($"holding {scene.HeldItems}");
                if (!string.IsNullOrWhiteSpace(scene.Atmosphere)) sceneTags.Add(scene.Atmosphere);
            }

            sceneTags.AddRange(expressionTags);
        }

        var identityPart = string.Join(", ", characterTags.Where(t => !string.IsNullOrWhiteSpace(t)));
        var scenePart = string.Join(", ", sceneTags.Where(t => !string.IsNullOrWhiteSpace(t)));

        var parts = new List<string> { identityPart, scenePart, "cinematic composition, dramatic lighting, detailed background, soft painterly anime aesthetic, 8k, pixiv trending" };
        return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string MapMoodToVisualExpression(CharacterMood mood, int intensity)
    {
        var intensitySuffix = intensity >= 70 ? ", strong emotional expression" : "";
        return mood switch
        {
            CharacterMood.Happy => "bright happy smile, sparkling eyes, cheerful look" + intensitySuffix,
            CharacterMood.Sad => "melancholy eyes, looking down, gentle sadness, soft frown" + intensitySuffix,
            CharacterMood.Angry => "pouting angrily, slight glare, stern gaze, flushed cheeks" + intensitySuffix,
            CharacterMood.Excited => "beaming joy, wide sparkling eyes, energetic expression" + intensitySuffix,
            CharacterMood.Anxious => "nervous expression, biting lower lip, worried eyes, fidgeting" + intensitySuffix,
            CharacterMood.Embarrassed => "heavy blush, shyly looking away, covering mouth, flustered" + intensitySuffix,
            CharacterMood.Curious => "inquisitive gaze, head tilted, observant eyes, slight smile" + intensitySuffix,
            CharacterMood.Affectionate => "deep affectionate gaze, warm loving smile, gentle blush, tender look" + intensitySuffix,
            CharacterMood.Playful => "mischievous wink, playful smirk, teasing expression" + intensitySuffix,
            _ => "calm neutral expression, soft observant eyes"
        };
    }
}
