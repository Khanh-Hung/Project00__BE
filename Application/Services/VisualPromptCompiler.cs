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
            var genderInvariant = GenderPromptInvariant.Resolve(identity.ResolvedGender);
            if (!string.IsNullOrWhiteSpace(genderInvariant.PositiveTokens))
            {
                traits.Add(genderInvariant.PositiveTokens);
            }

            if (!string.IsNullOrWhiteSpace(identity.AgeAppearance)) traits.Add(identity.AgeAppearance);
            if (!string.IsNullOrWhiteSpace(identity.Hair)) traits.Add(identity.Hair);
            if (!string.IsNullOrWhiteSpace(identity.Eyes)) traits.Add(identity.Eyes);
            if (!string.IsNullOrWhiteSpace(identity.Face)) traits.Add(identity.Face);
            if (!string.IsNullOrWhiteSpace(identity.Skin)) traits.Add(identity.Skin);
            if (!string.IsNullOrWhiteSpace(identity.Body)) traits.Add(identity.Body);
            if (!string.IsNullOrWhiteSpace(identity.ClothingStyle)) traits.Add(identity.ClothingStyle);
            if (!string.IsNullOrWhiteSpace(identity.Accessories)) traits.Add(identity.Accessories);
            if (!string.IsNullOrWhiteSpace(identity.VisualTraits)) traits.Add(identity.VisualTraits);

            if (identity.SignatureFeatures != null)
            {
                foreach (var feature in identity.SignatureFeatures)
                {
                    if (!string.IsNullOrWhiteSpace(feature.PositiveTokens))
                    {
                        traits.Add(feature.PositiveTokens);
                    }
                }
            }
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
            var genderInvariant = GenderPromptInvariant.Resolve(identity.ResolvedGender);
            if (!string.IsNullOrWhiteSpace(genderInvariant.PositiveTokens))
            {
                characterTags.Add(genderInvariant.PositiveTokens);
            }

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

            if (identity.SignatureFeatures != null)
            {
                foreach (var feature in identity.SignatureFeatures)
                {
                    if (feature.ShouldInject(isSameScene: true))
                    {
                        if (!string.IsNullOrWhiteSpace(feature.PositiveTokens))
                        {
                            characterTags.Add(feature.PositiveTokens);
                        }
                    }
                }
            }
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

        var allTags = new List<string>();

        // Tier 1: Base Quality & Character Physical Identity Foundation
        allTags.Add("masterpiece, best quality, solo");
        var identity = snapshot.VisualIdentity;
        if (identity != null)
        {
            var genderInvariant = GenderPromptInvariant.Resolve(identity.ResolvedGender);
            if (!string.IsNullOrWhiteSpace(genderInvariant.PositiveTokens))
            {
                allTags.Add(genderInvariant.PositiveTokens);
            }

            if (!string.IsNullOrWhiteSpace(identity.AgeAppearance)) allTags.Add(identity.AgeAppearance);
            if (!string.IsNullOrWhiteSpace(identity.Hair)) allTags.Add(identity.Hair);
            if (!string.IsNullOrWhiteSpace(identity.Eyes)) allTags.Add(identity.Eyes);
            if (!string.IsNullOrWhiteSpace(identity.Face)) allTags.Add(identity.Face);
            if (!string.IsNullOrWhiteSpace(identity.Skin)) allTags.Add(identity.Skin);
            if (!string.IsNullOrWhiteSpace(identity.Body)) allTags.Add(identity.Body);
            if (!string.IsNullOrWhiteSpace(identity.Accessories)) allTags.Add(identity.Accessories);
            if (!string.IsNullOrWhiteSpace(identity.VisualTraits)) allTags.Add(identity.VisualTraits);

            // Canonical Signature Feature Invariant Injection (Prevents micro-feature dilution)
            if (identity.SignatureFeatures != null)
            {
                foreach (var feature in identity.SignatureFeatures)
                {
                    if (feature.ShouldInject(snapshot.Context))
                    {
                        if (!string.IsNullOrWhiteSpace(feature.PositiveTokens))
                        {
                            allTags.Add(feature.PositiveTokens);
                        }
                    }
                }
            }
        }
        else
        {
            allTags.Add("1girl");
        }

        // Tier 2: Persistent Scene State (Room, Position, Outfit, Time, Held Items)
        var scene = snapshot.SceneState;
        if (scene != null)
        {
            if (!string.IsNullOrWhiteSpace(scene.CurrentOutfit))
                allTags.Add($"wearing {scene.CurrentOutfit}");
            else if (!string.IsNullOrWhiteSpace(identity?.ClothingStyle))
                allTags.Add($"wearing {identity.ClothingStyle}");

            if (!string.IsNullOrWhiteSpace(scene.CurrentPosition) && !string.IsNullOrWhiteSpace(scene.CurrentLocation))
                allTags.Add($"at {scene.CurrentPosition}, in {scene.CurrentLocation}");
            else if (!string.IsNullOrWhiteSpace(scene.CurrentPosition))
                allTags.Add($"at {scene.CurrentPosition}");
            else if (!string.IsNullOrWhiteSpace(scene.CurrentLocation))
                allTags.Add($"in {scene.CurrentLocation}");

            if (!string.IsNullOrWhiteSpace(scene.CurrentTimeOfDay)) allTags.Add(scene.CurrentTimeOfDay);
            if (!string.IsNullOrWhiteSpace(scene.HeldItems) && !scene.HeldItems.Equals("none", StringComparison.OrdinalIgnoreCase))
                allTags.Add($"holding {scene.HeldItems}");
            if (!string.IsNullOrWhiteSpace(scene.Atmosphere)) allTags.Add(scene.Atmosphere);
        }
        else if (!string.IsNullOrWhiteSpace(identity?.ClothingStyle))
        {
            allTags.Add($"wearing {identity.ClothingStyle}");
        }

        // Tier 3: Transient Action, Pose, Expression & Gaze
        var transient = snapshot.TransientState;
        if (transient != null)
        {
            if (!string.IsNullOrWhiteSpace(transient.Expression)) allTags.Add(transient.Expression);
            if (!string.IsNullOrWhiteSpace(transient.Gaze)) allTags.Add(transient.Gaze);
            if (!string.IsNullOrWhiteSpace(transient.Pose)) allTags.Add(transient.Pose);
            if (!string.IsNullOrWhiteSpace(transient.Action)) allTags.Add(transient.Action);
            if (!string.IsNullOrWhiteSpace(transient.Gesture)) allTags.Add(transient.Gesture);
            if (!string.IsNullOrWhiteSpace(transient.Interaction)) allTags.Add(transient.Interaction);
        }

        // Tier 4: Structured Cinematic Composition & Narrative Understanding
        var sceneDesc = snapshot.SceneDescription;
        if (sceneDesc != null)
        {
            if (!string.IsNullOrWhiteSpace(sceneDesc.ShotType)) allTags.Add(sceneDesc.ShotType);
            if (!string.IsNullOrWhiteSpace(sceneDesc.CameraAngle)) allTags.Add(sceneDesc.CameraAngle);
            if (!string.IsNullOrWhiteSpace(sceneDesc.SubjectPlacement)) allTags.Add(sceneDesc.SubjectPlacement);
            if (!string.IsNullOrWhiteSpace(sceneDesc.DetailedAction)) allTags.Add(sceneDesc.DetailedAction);
            if (!string.IsNullOrWhiteSpace(sceneDesc.DetailedEnvironment)) allTags.Add(sceneDesc.DetailedEnvironment);
            if (!string.IsNullOrWhiteSpace(sceneDesc.LightingStyle)) allTags.Add(sceneDesc.LightingStyle);
            if (!string.IsNullOrWhiteSpace(sceneDesc.Atmosphere)) allTags.Add(sceneDesc.Atmosphere);

            if (sceneDesc.EnglishPromptTags != null && !sceneDesc.EnglishPromptTags.IsDefaultOrEmpty)
            {
                allTags.AddRange(sceneDesc.EnglishPromptTags.Where(t => !string.IsNullOrWhiteSpace(t)));
            }
        }

        // Tier 5: Aesthetic Quality & Style Anchors
        allTags.Add("cinematic composition, dramatic lighting, detailed background, soft painterly anime aesthetic, 8k, pixiv trending");

        var cleanTags = DeduplicateAndClean(allTags);
        return string.Join(", ", cleanTags);
    }

    /// <summary>
    /// Compiles deterministic negative invariant prompt with generic gender-opposing tokens and signature feature exclusions.
    /// </summary>
    public string CompileNegativePrompt(VisualSnapshot snapshot, string? customNegative = null)
    {
        return CompileNegativePrompt(snapshot?.VisualIdentity, customNegative ?? snapshot?.NegativeConstraints);
    }

    /// <summary>
    /// Compiles deterministic negative invariant prompt with generic gender-opposing tokens and signature feature exclusions.
    /// </summary>
    public string CompileNegativePrompt(CharacterVisualIdentity? identity, string? customNegative = null)
    {
        var negativeTags = new List<string>();

        // Tier 1: Generic Quality & Multi-subject Artifact Negative Anchor
        negativeTags.Add("2girls, 2boys, multiple people, group, crowd, duo, couple, 2persons, extra person, bad anatomy, bad hands, missing fingers, extra digits, cropped, signature, watermark, blurry, low quality, worst quality");

        // Tier 2: Generic Gender-Opposing Invariant Gating
        if (identity != null)
        {
            var genderInvariant = GenderPromptInvariant.Resolve(identity.ResolvedGender);
            if (!string.IsNullOrWhiteSpace(genderInvariant.NegativeTokens))
            {
                negativeTags.Add(genderInvariant.NegativeTokens);
            }

            // Tier 3: Feature-specific negative tokens
            if (identity.SignatureFeatures != null)
            {
                foreach (var feature in identity.SignatureFeatures)
                {
                    if (!string.IsNullOrWhiteSpace(feature.NegativeTokens))
                    {
                        negativeTags.Add(feature.NegativeTokens);
                    }
                }
            }
        }

        // Tier 4: Custom negative constraints if provided
        if (!string.IsNullOrWhiteSpace(customNegative))
        {
            negativeTags.Add(customNegative);
        }

        var cleanNegatives = DeduplicateAndClean(negativeTags);
        return string.Join(", ", cleanNegatives);
    }

    private static List<string> DeduplicateAndClean(IEnumerable<string> tags)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var subTags = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var sub in subTags)
            {
                if (string.IsNullOrWhiteSpace(sub)) continue;
                var cleanSub = sub.Trim();
                if (seen.Add(cleanSub))
                {
                    result.Add(cleanSub);
                }
            }
        }

        return result;
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
