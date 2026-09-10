using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace Application.Services;

public sealed class SceneGenerationRequestMapper
{
    private readonly IConfiguration? _configuration;

    public SceneGenerationRequestMapper(IConfiguration? configuration = null)
    {
        _configuration = configuration;
    }

    public VisualSnapshot MapToVisualSnapshot(
        SceneSpecification scene,
        VisualContextResolutionResult visualContext,
        GenerationProfile generationProfile,
        IScenePromptComposer promptComposer,
        CharacterVisualIdentity? explicitIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(scene, nameof(scene));
        ArgumentNullException.ThrowIfNull(visualContext, nameof(visualContext));
        ArgumentNullException.ThrowIfNull(generationProfile, nameof(generationProfile));
        ArgumentNullException.ThrowIfNull(promptComposer, nameof(promptComposer));

        var prompt = promptComposer.ComposePrompt(scene, visualContext);

        var sceneDesc = new VisualSceneDescription(
            shotType: scene.Camera,
            detailedAction: scene.Action,
            detailedEnvironment: scene.Environment?.Architecture ?? scene.Location,
            lightingStyle: scene.Lighting,
            atmosphere: scene.Mood,
            englishPromptTags: new[] { prompt.PositivePrompt }
        );

        var sourceIdentity = explicitIdentity ?? visualContext.VisualIdentity;

        // Precedence: Explicit Character Style > Configured DefaultStyle > Unspecified
        string? effectiveStyle = null;
        var effectiveVisualStyle = VisualStyle.Unspecified;

        if (!string.IsNullOrWhiteSpace(sourceIdentity?.Style))
        {
            effectiveStyle = sourceIdentity.Style.Trim();
            if (sourceIdentity.VisualStyle != VisualStyle.Unspecified)
            {
                effectiveVisualStyle = sourceIdentity.VisualStyle;
            }
            else if (Enum.TryParse<VisualStyle>(effectiveStyle, ignoreCase: true, out var parsedStyle))
            {
                effectiveVisualStyle = parsedStyle;
            }
        }
        else if (sourceIdentity?.VisualStyle is { } vs and not VisualStyle.Unspecified)
        {
            effectiveVisualStyle = vs;
            effectiveStyle = vs.ToString();
        }
        else
        {
            var configuredDefaultStyle = _configuration?["AiProviders:ImageGeneration:DefaultStyle"]?.Trim();
            if (!string.IsNullOrWhiteSpace(configuredDefaultStyle))
            {
                effectiveStyle = configuredDefaultStyle;
                if (Enum.TryParse<VisualStyle>(configuredDefaultStyle, ignoreCase: true, out var parsedDefaultStyle))
                {
                    effectiveVisualStyle = parsedDefaultStyle;
                }
            }
        }

        CharacterVisualIdentity? identity = null;
        if (visualContext.CurrentAppearance != null)
        {
            identity = new CharacterVisualIdentity(
                Hair: visualContext.CurrentAppearance.HairColor ?? sourceIdentity?.Hair,
                Eyes: visualContext.CurrentAppearance.EyeColor ?? sourceIdentity?.Eyes,
                Skin: visualContext.CurrentAppearance.SkinTone ?? sourceIdentity?.Skin,
                ClothingStyle: scene.OutfitContext ?? visualContext.CurrentAppearance.CurrentOutfit ?? sourceIdentity?.ClothingStyle,
                Face: sourceIdentity?.Face,
                Body: sourceIdentity?.Body,
                Accessories: sourceIdentity?.Accessories,
                CanonicalReferenceUrl: visualContext.CanonicalIdentityReference?.ReferenceUrl ?? sourceIdentity?.CanonicalReferenceUrl,
                FullBodyUrl: sourceIdentity?.FullBodyUrl,
                Gender: sourceIdentity?.Gender,
                Style: effectiveStyle,
                VisualStyle: effectiveVisualStyle
            );
        }
        else if (sourceIdentity != null)
        {
            identity = sourceIdentity with
            {
                ClothingStyle = scene.OutfitContext ?? sourceIdentity.ClothingStyle,
                CanonicalReferenceUrl = visualContext.CanonicalIdentityReference?.ReferenceUrl ?? sourceIdentity.CanonicalReferenceUrl,
                Style = effectiveStyle,
                VisualStyle = effectiveVisualStyle
            };
        }
        else if (!string.IsNullOrWhiteSpace(effectiveStyle) || effectiveVisualStyle != VisualStyle.Unspecified)
        {
            identity = new CharacterVisualIdentity(
                ClothingStyle: scene.OutfitContext,
                CanonicalReferenceUrl: visualContext.CanonicalIdentityReference?.ReferenceUrl,
                Style: effectiveStyle,
                VisualStyle: effectiveVisualStyle
            );
        }

        var sessionState = new SessionSceneState(
            CurrentLocation: scene.Location,
            CurrentTimeOfDay: scene.TimeOfDay,
            Atmosphere: scene.Mood,
            SceneRevision: scene.SceneRevision
        );

        var slot2Context = visualContext.TransitionType == SceneTransitionType.SameScene && visualContext.PredecessorVisualMemory != null
            ? Slot2Context.SameScene
            : Slot2Context.ColdStart;

        var conditioningIntent = identity?.CreateConditioningIntent(
            previousSceneReferenceUrl: null,
            context: slot2Context
        ) ?? IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: visualContext.CanonicalIdentityReference?.ReferenceUrl,
            previousSceneReferenceUrl: null,
            context: slot2Context
        );

        return VisualSnapshot.Create(
            turnId: scene.TurnId ?? Guid.Empty,
            sessionId: scene.SessionId ?? Guid.Empty,
            characterId: scene.CharacterId,
            sceneRevision: scene.SceneRevision,
            visualIdentity: identity,
            sceneState: sessionState,
            transientState: null,
            generationProfile: generationProfile,
            previousSceneImageUrl: null,
            predecessorSceneRevision: scene.SceneRevision > 1 ? scene.SceneRevision - 1 : null,
            predecessorSceneImageId: visualContext.PredecessorVisualMemory?.ArtifactId,
            negativeConstraints: prompt.NegativePrompt,
            fallbackReferenceUrl: visualContext.CanonicalIdentityReference?.ReferenceUrl,
            sceneDescription: sceneDesc,
            slot2Context: slot2Context,
            identityConditioning: conditioningIntent
        );
    }
}
