using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Policies;
using Domain.ValueObjects;
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
        SceneVisualState? visualState = null,
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

        string location;
        string action;
        string pose;
        string weather;
        string timeOfDay;
        string lighting;
        string camera;
        string mood;
        string? outfit;
        List<string> props;
        string? architecture = intent.EnvironmentHint;

        if (visualState != null)
        {
            // Authoritative consumption of resolved SceneVisualState
            location = visualState.Location;
            action = visualState.CharacterState.Action ?? intent.ActionHint ?? "standing calmly";
            pose = visualState.CharacterState.Pose ?? "standing naturally";
            weather = visualState.Weather;
            timeOfDay = visualState.TimeOfDay;
            lighting = visualState.Lighting;
            mood = visualState.Atmosphere;
            outfit = visualState.CharacterState.Outfit;
            props = new List<string>(visualState.Props);

            camera = !string.IsNullOrWhiteSpace(intent.CameraHint) 
                ? intent.CameraHint.Trim() 
                : "medium cinematic shot, eye-level angle, centered subject placement";
        }
        else
        {
            // Fallback path if visualState was not explicitly passed
            var sceneLocation = new SceneLocation(intent.LocationHint);
            var sceneAction = new SceneAction(intent.ActionHint);
            location = sceneLocation.Value;
            action = sceneAction.Value;

            var transitionType = VisualContinuityPolicy.EvaluateTransition(
                previousLocation: context.PreviousScene?.Location,
                currentLocation: location,
                previousAction: context.PreviousScene?.Action,
                currentAction: action
            );

            if (!string.IsNullOrWhiteSpace(intent.PoseHint))
            {
                pose = intent.PoseHint.Trim();
            }
            else
            {
                var actionLower = action.ToLowerInvariant();
                if (actionLower.Contains("read") || actionLower.Contains("sit") || actionLower.Contains("rest") || actionLower.Contains("eat") || actionLower.Contains("drink")
                    || actionLower.Contains("đọc") || actionLower.Contains("ngồi") || actionLower.Contains("uống") || actionLower.Contains("ăn"))
                {
                    pose = "seated naturally";
                }
                else if (actionLower.Contains("sleep") || actionLower.Contains("lie") || actionLower.Contains("lay") || actionLower.Contains("nằm") || actionLower.Contains("ngủ"))
                {
                    pose = "lying down relaxed";
                }
                else if (actionLower.Contains("walk") || actionLower.Contains("run") || actionLower.Contains("stride") || actionLower.Contains("bước") || actionLower.Contains("chạy") || actionLower.Contains("đi"))
                {
                    pose = "mid-stride dynamic movement";
                }
                else
                {
                    pose = "standing naturally";
                }
            }

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

            if (!string.IsNullOrWhiteSpace(intent.LightingHint))
            {
                lighting = intent.LightingHint.Trim();
            }
            else
            {
                var timeLower = timeOfDay.ToLowerInvariant();
                var isNight = timeLower.Contains("night") || timeLower.Contains("midnight") || timeLower.Contains("đêm");
                var isSunset = timeLower.Contains("sunset") || timeLower.Contains("golden") || timeLower.Contains("dusk") || timeLower.Contains("hoàng hôn");

                if (sceneLocation.IsOutdoors)
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

            camera = !string.IsNullOrWhiteSpace(intent.CameraHint) 
                ? intent.CameraHint.Trim() 
                : "medium cinematic shot, eye-level angle, centered subject placement";

            props = new List<string>(intent.ObjectHints);
            if (transitionType == Domain.Enums.SceneTransitionType.SameScene && context.PreviousScene?.Environment != null)
            {
                if (string.IsNullOrWhiteSpace(architecture))
                {
                    architecture = context.PreviousScene.Environment.Architecture;
                }
                foreach (var prevProp in context.PreviousScene.Environment.Props)
                {
                    if (!props.Contains(prevProp, StringComparer.OrdinalIgnoreCase))
                    {
                        props.Add(prevProp);
                    }
                }
            }

            mood = !string.IsNullOrWhiteSpace(intent.MoodHint) ? intent.MoodHint.Trim() : "neutral cinematic";
            outfit = !string.IsNullOrWhiteSpace(intent.OutfitHint)
                ? intent.OutfitHint.Trim()
                : context.CharacterVisualProfile?.CurrentOutfit;
        }

        var sceneEnvironment = SceneEnvironment.Create(
            location: location,
            architecture: architecture,
            props: props,
            weather: weather,
            timeOfDay: timeOfDay,
            lighting: lighting,
            atmosphere: mood
        );

        var spec = new SceneSpecification(
            characterId: context.CharacterId,
            location: location,
            action: action,
            sceneRevision: context.SceneRevision,
            sessionId: context.SessionId,
            turnId: context.TurnId,
            pose: pose,
            environment: sceneEnvironment,
            lighting: lighting,
            camera: camera,
            weather: weather,
            timeOfDay: timeOfDay,
            mood: mood,
            outfitContext: outfit
        );

        _logger.LogInformation(
            "[SceneComposer] Composed SceneSpecification Id={SceneId}, CharacterId={CharacterId}, Location='{Location}', Action='{Action}', Fingerprint='{Fingerprint}', Revision={Revision}",
            spec.Id, spec.CharacterId, spec.Location, spec.Action, spec.SceneFingerprint, spec.SceneRevision);

        return Task.FromResult(spec);
    }
}
