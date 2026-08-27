using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Services;

public sealed class SceneGenerationRequestMapper
{
    public VisualSnapshot MapToVisualSnapshot(
        SceneSpecification scene,
        VisualContextResolutionResult visualContext,
        GenerationProfile generationProfile,
        IScenePromptComposer promptComposer)
    {
        ArgumentNullException.ThrowIfNull(scene, nameof(scene));
        ArgumentNullException.ThrowIfNull(visualContext, nameof(visualContext));
        ArgumentNullException.ThrowIfNull(generationProfile, nameof(generationProfile));
        ArgumentNullException.ThrowIfNull(promptComposer, nameof(promptComposer));

        var prompt = promptComposer.ComposePrompt(scene, visualContext);

        var sceneDesc = new VisualSceneDescription(
            shotType: scene.Camera,
            detailedAction: scene.Action,
            detailedEnvironment: scene.Environment ?? scene.Location,
            lightingStyle: scene.Lighting,
            atmosphere: scene.Mood,
            englishPromptTags: new[] { prompt.PositivePrompt }
        );

        var identity = visualContext.CurrentAppearance != null
            ? new CharacterVisualIdentity(
                Hair: visualContext.CurrentAppearance.HairColor,
                Eyes: visualContext.CurrentAppearance.EyeColor,
                Skin: visualContext.CurrentAppearance.SkinTone,
                ClothingStyle: scene.OutfitContext ?? visualContext.CurrentAppearance.CurrentOutfit,
                CanonicalReferenceUrl: visualContext.CanonicalIdentityReference?.ReferenceUrl,
                FullBodyUrl: null
            )
            : null;

        var sessionState = new SessionSceneState(
            CurrentLocation: scene.Location,
            CurrentTimeOfDay: scene.TimeOfDay,
            Atmosphere: scene.Mood,
            SceneRevision: scene.SceneRevision
        );

        var slot2Context = visualContext.TransitionType == SceneTransitionType.SameScene && visualContext.PredecessorVisualMemory != null
            ? Slot2Context.SameScene
            : Slot2Context.ColdStart;

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
            slot2Context: slot2Context
        );
    }
}
