using Application.Contracts.Activities;
using Domain.Entities;
using Domain.Enums;

namespace Application.Services;

/// <summary>
/// Authoritative bridge mapping a CharacterActivity into a structured SceneIntent for the visual pipeline.
/// Does not compile prompts manually; populates pure domain intent fields.
/// </summary>
public static class CharacterActivitySceneIntentMapper
{
    public static SceneIntent MapToSceneIntent(
        CharacterActivity activity,
        CharacterActivityCandidate candidate,
        CharacterVisualState? currentVisualState = null,
        Guid? sessionId = null,
        Guid? turnId = null)
    {
        ArgumentNullException.ThrowIfNull(activity, nameof(activity));
        ArgumentNullException.ThrowIfNull(candidate, nameof(candidate));

        var mood = activity.ActivityType switch
        {
            CharacterActivityType.Sleeping => "Peaceful",
            CharacterActivityType.GettingReady => "Poised and focused",
            CharacterActivityType.Exploring => "Curious and adventurous",
            CharacterActivityType.Exercising => "Determined and athletic",
            CharacterActivityType.Bathing => "Serene and relaxed",
            CharacterActivityType.Cooking => "Engaged and warm",
            CharacterActivityType.Reading or CharacterActivityType.Working => "Scholarly contemplative",
            _ => "Neutral"
        };

        var effectiveTurnId = turnId ?? activity.Id;

        return new SceneIntent(
            characterId: activity.CharacterId,
            locationHint: activity.Location,
            actionHint: candidate.ActionHint,
            sessionId: sessionId,
            turnId: effectiveTurnId,
            poseHint: candidate.PoseHint,
            environmentHint: candidate.EnvironmentHint,
            lightingHint: null,
            cameraHint: null,
            weatherHint: null,
            timeOfDayHint: null,
            moodHint: mood,
            outfitHint: candidate.OutfitHint ?? currentVisualState?.Outfit,
            hairstyleHint: currentVisualState?.Hairstyle,
            objectHints: null,
            atmosphereHints: null
        );
    }
}
