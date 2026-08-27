using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Policies;
using Domain.ValueObjects.Scene;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class SceneComposer : ISceneComposer
{
    private readonly ILogger<SceneComposer> _logger;

    public SceneComposer(ILogger<SceneComposer> logger)
    {
        _logger = logger;
    }

    public Task<SceneSpecification> ComposeAsync(
        SceneIntent intent,
        SceneCompositionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent, nameof(intent));
        ArgumentNullException.ThrowIfNull(context, nameof(context));

        if (intent.CharacterId != context.CharacterId)
        {
            throw new ArgumentException(
                $"Character mismatch: Intent character '{intent.CharacterId}' does not match context character '{context.CharacterId}'.",
                nameof(intent));
        }

        // 1. Location and Action normalization
        var location = new SceneLocation(intent.LocationHint);
        var action = new SceneAction(intent.ActionHint);

        // 2. Evaluate scene continuity against previous scene
        var transitionType = SceneContinuityPolicy.EvaluateTransition(
            previousLocation: context.PreviousScene?.Location,
            currentLocation: location.Value,
            previousAction: context.PreviousScene?.Action,
            currentAction: action.Value
        );

        // 3. Resolve Pose
        string pose;
        if (!string.IsNullOrWhiteSpace(intent.PoseHint))
        {
            pose = intent.PoseHint.Trim();
        }
        else
        {
            var actionLower = action.Value.ToLowerInvariant();
            if (actionLower.Contains("read") || actionLower.Contains("sit") || actionLower.Contains("rest") || actionLower.Contains("eat") || actionLower.Contains("drink"))
            {
                pose = "seated naturally";
            }
            else if (actionLower.Contains("sleep") || actionLower.Contains("lie") || actionLower.Contains("lay"))
            {
                pose = "lying down relaxed";
            }
            else if (actionLower.Contains("walk") || actionLower.Contains("run") || actionLower.Contains("stride"))
            {
                pose = "mid-stride dynamic movement";
            }
            else
            {
                pose = "standing naturally";
            }
        }

        // 4. Resolve Weather & Time of Day with continuity
        string weather;
        if (!string.IsNullOrWhiteSpace(intent.WeatherHint))
        {
            weather = intent.WeatherHint.Trim();
        }
        else if (transitionType == Domain.Enums.SceneTransitionType.SameScene && !string.IsNullOrWhiteSpace(context.PreviousScene?.Weather))
        {
            weather = context.PreviousScene.Weather;
        }
        else
        {
            weather = "clear";
        }

        string timeOfDay;
        if (!string.IsNullOrWhiteSpace(intent.TimeOfDayHint))
        {
            timeOfDay = intent.TimeOfDayHint.Trim();
        }
        else if (transitionType == Domain.Enums.SceneTransitionType.SameScene && !string.IsNullOrWhiteSpace(context.PreviousScene?.TimeOfDay))
        {
            timeOfDay = context.PreviousScene.TimeOfDay;
        }
        else
        {
            timeOfDay = "daytime";
        }

        // 5. Resolve Lighting
        string lighting;
        if (!string.IsNullOrWhiteSpace(intent.LightingHint))
        {
            lighting = intent.LightingHint.Trim();
        }
        else
        {
            var timeLower = timeOfDay.ToLowerInvariant();
            var isNight = timeLower.Contains("night") || timeLower.Contains("midnight") || timeLower.Contains("đêm");
            var isSunset = timeLower.Contains("sunset") || timeLower.Contains("golden") || timeLower.Contains("dusk") || timeLower.Contains("hoàng hôn");

            if (location.IsOutdoors)
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
        }

        // 6. Resolve Camera
        string camera;
        if (!string.IsNullOrWhiteSpace(intent.CameraHint))
        {
            camera = intent.CameraHint.Trim();
        }
        else
        {
            camera = "medium cinematic shot, eye-level angle, centered subject placement";
        }

        // 7. Resolve Environment
        string? environment = intent.EnvironmentHint;
        if (string.IsNullOrWhiteSpace(environment))
        {
            if (transitionType == Domain.Enums.SceneTransitionType.SameScene && !string.IsNullOrWhiteSpace(context.PreviousScene?.Environment))
            {
                environment = context.PreviousScene.Environment;
            }
            else
            {
                environment = $"atmospheric {location.Value} setting";
            }
        }

        // 8. Resolve Mood & Outfit
        var mood = !string.IsNullOrWhiteSpace(intent.MoodHint) ? intent.MoodHint.Trim() : "neutral cinematic";
        var outfit = !string.IsNullOrWhiteSpace(intent.OutfitHint)
            ? intent.OutfitHint.Trim()
            : context.CharacterVisualProfile?.CurrentOutfit;

        // 9. Collect Objects and Atmosphere Elements
        var objects = new List<string>(intent.ObjectHints);
        if (transitionType == Domain.Enums.SceneTransitionType.SameScene && context.PreviousScene != null)
        {
            foreach (var prevObj in context.PreviousScene.Objects)
            {
                if (!objects.Contains(prevObj, StringComparer.OrdinalIgnoreCase))
                {
                    objects.Add(prevObj);
                }
            }
        }

        var atmosphere = new List<string>(intent.AtmosphereHints);

        var spec = new SceneSpecification(
            characterId: context.CharacterId,
            location: location.Value,
            action: action.Value,
            sceneRevision: context.SceneRevision,
            sessionId: context.SessionId,
            turnId: context.TurnId,
            pose: pose,
            environment: environment,
            lighting: lighting,
            camera: camera,
            weather: weather,
            timeOfDay: timeOfDay,
            mood: mood,
            outfitContext: outfit,
            objects: objects,
            atmosphereElements: atmosphere
        );

        _logger.LogInformation(
            "[SceneComposer] Composed SceneSpecification Id={SceneId}, CharacterId={CharacterId}, Location='{Location}', Action='{Action}', Revision={Revision}, Transition={Transition}",
            spec.Id, spec.CharacterId, spec.Location, spec.Action, spec.SceneRevision, transitionType);

        return Task.FromResult(spec);
    }
}
