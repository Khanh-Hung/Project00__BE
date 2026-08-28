using Application.Common.Exceptions;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects.Scene;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class VisualContinuityResolver : IVisualContinuityResolver
{
    private readonly ISceneVisualStateReader? _stateReader;
    private readonly ILogger<VisualContinuityResolver> _logger;

    public VisualContinuityResolver(
        ISceneVisualStateReader? stateReader,
        ILogger<VisualContinuityResolver> logger)
    {
        _stateReader = stateReader;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public VisualContinuityResolver(ILogger<VisualContinuityResolver> logger)
        : this(null, logger)
    {
    }

    public async Task<VisualContinuityResult> ResolveAsync(
        VisualContinuityRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request, nameof(request));
        var intent = request.Intent ?? throw new ArgumentException("Intent cannot be null.", nameof(request));
        var context = request.Context ?? throw new ArgumentException("Context cannot be null.", nameof(request));
        var targetRevision = request.TargetRevision;

        var locationStr = !string.IsNullOrWhiteSpace(intent.LocationHint)
            ? intent.LocationHint.Trim()
            : (context.PreviousScene?.Location ?? "Sanctuary");
        var sceneLocation = new SceneLocation(locationStr);
        var sceneKey = SceneVisualState.NormalizeSceneKey(sceneLocation.Value);

        // 1. Query State Reader if available for active session state & historical re-entry state
        SceneVisualState? latestSessionState = null;
        SceneVisualState? reenteredHistoricalState = null;

        if (_stateReader != null && intent.SessionId.HasValue)
        {
            try
            {
                latestSessionState = await _stateReader.GetLatestBySessionAsync(intent.SessionId.Value, ct);
                reenteredHistoricalState = await _stateReader.GetLatestBySessionAndSceneKeyAsync(intent.SessionId.Value, sceneKey, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VisualContinuityResolver] Failed to load state records from reader for SessionId={SessionId}. Using context fallback.", intent.SessionId);
            }
        }

        // 2. Synthesize previous state from Context.PreviousScene if no DB record was found
        if (latestSessionState == null && context.PreviousScene != null)
        {
            var prevCharState = new CharacterVisualState(
                characterId: context.CharacterId,
                location: context.PreviousScene.Location,
                sceneRevision: context.PreviousScene.SceneRevision,
                outfit: context.PreviousScene.OutfitContext ?? context.CharacterVisualProfile?.CurrentOutfit,
                hairstyle: context.CharacterVisualProfile?.Hairstyle,
                pose: context.PreviousScene.Pose,
                action: context.PreviousScene.Action,
                activeProps: context.PreviousScene.Environment?.Props,
                sourceTurnId: context.TurnId
            );

            latestSessionState = new SceneVisualState(
                sessionId: intent.SessionId ?? Guid.Empty,
                characterId: context.CharacterId,
                location: context.PreviousScene.Location,
                characterState: prevCharState,
                sceneRevision: context.PreviousScene.SceneRevision,
                timeOfDay: context.PreviousScene.TimeOfDay,
                weather: context.PreviousScene.Weather,
                lighting: context.PreviousScene.Lighting,
                atmosphere: context.PreviousScene.Mood,
                props: context.PreviousScene.Environment?.Props,
                sourceTurnId: context.TurnId
            );
        }

        // 3. Evaluate Transition Type
        var previousLocationStr = latestSessionState?.Location ?? context.PreviousScene?.Location;
        bool hasHistoricalState = reenteredHistoricalState != null && !string.Equals(reenteredHistoricalState.Location, previousLocationStr, StringComparison.OrdinalIgnoreCase);

        var transitionType = VisualContinuityPolicy.EvaluateTransition(
            previousLocation: previousLocationStr,
            currentLocation: sceneLocation.Value,
            hasHistoricalStateForCurrentLocation: hasHistoricalState,
            previousAction: latestSessionState?.CharacterState.Action ?? context.PreviousScene?.Action,
            currentAction: intent.ActionHint
        );

        // 4. Resolve Character Outfit (CurrentIntent > PreviousSceneState > ActiveVisualMemory > ProfileDefault)
        var activeValidMemory = context.RelevantVisualMemories?.FirstOrDefault(m => m.ValidUntilTurnId == null) 
                                ?? (context.PreviousAcceptedVisualMemory?.ValidUntilTurnId == null ? context.PreviousAcceptedVisualMemory : null);
        var (outfit, outfitSource) = VisualContinuityPolicy.ResolveOutfit(
            intentOutfit: intent.OutfitHint,
            previousSceneOutfit: latestSessionState?.CharacterState.Outfit ?? context.PreviousScene?.OutfitContext,
            activeMemoryContext: activeValidMemory?.Context,
            profileDefaultOutfit: context.CharacterVisualProfile?.CurrentOutfit
        );

        // 5. Resolve Hairstyle
        var (hairstyle, hairSource) = VisualContinuityPolicy.ResolveHairstyle(
            intentHairstyle: null,
            previousSceneHairstyle: latestSessionState?.CharacterState.Hairstyle ?? context.CharacterVisualProfile?.Hairstyle,
            profileDefaultHairstyle: context.CharacterVisualProfile?.Hairstyle
        );

        // 6. Resolve Pose & Action
        var (pose, action, poseActionSource) = VisualContinuityPolicy.ResolvePoseAndAction(
            intentPose: intent.PoseHint,
            intentAction: intent.ActionHint,
            previousPose: latestSessionState?.CharacterState.Pose ?? context.PreviousScene?.Pose,
            previousAction: latestSessionState?.CharacterState.Action ?? context.PreviousScene?.Action,
            transitionType: transitionType
        );

        // 7. Resolve Environment (Weather, TimeOfDay, Lighting)
        var (weather, timeOfDay, lighting, envSource) = VisualContinuityPolicy.ResolveEnvironment(
            intentWeather: intent.WeatherHint,
            intentTimeOfDay: intent.TimeOfDayHint,
            intentLighting: intent.LightingHint,
            previousState: transitionType == SceneTransitionType.SceneReentry ? reenteredHistoricalState : latestSessionState,
            transitionType: transitionType,
            isOutdoors: sceneLocation.IsOutdoors
        );

        // 8. Resolve Props & World Mutations
        var (props, persistentChanges, propsSource) = VisualContinuityPolicy.ResolvePropsAndWorldMutations(
            intentProps: intent.ObjectHints,
            previousState: latestSessionState,
            reenteredHistoricalState: reenteredHistoricalState,
            transitionType: transitionType
        );

        var atmosphere = !string.IsNullOrWhiteSpace(intent.MoodHint) ? intent.MoodHint.Trim() : (latestSessionState?.Atmosphere ?? "Neutral cinematic");
        var atmosphereSource = !string.IsNullOrWhiteSpace(intent.MoodHint) ? "CurrentIntent" : "PreviousSceneState";

        // 9. Delta Field Tracking
        var changedFields = new List<string>();
        var preservedFields = new List<string>();
        var invalidatedFields = new List<string>();

        if (latestSessionState != null)
        {
            if (string.Equals(outfit, latestSessionState.CharacterState.Outfit, StringComparison.OrdinalIgnoreCase))
                preservedFields.Add("Outfit");
            else
            {
                changedFields.Add("Outfit");
                invalidatedFields.Add("PreviousOutfit");
            }

            if (string.Equals(sceneLocation.Value, latestSessionState.Location, StringComparison.OrdinalIgnoreCase))
                preservedFields.Add("Location");
            else
            {
                changedFields.Add("Location");
                invalidatedFields.Add("PreviousLocation");
            }

            if (string.Equals(pose, latestSessionState.CharacterState.Pose, StringComparison.OrdinalIgnoreCase))
                preservedFields.Add("Pose");
            else
                changedFields.Add("Pose");

            if (string.Equals(action, latestSessionState.CharacterState.Action, StringComparison.OrdinalIgnoreCase))
                preservedFields.Add("Action");
            else
                changedFields.Add("Action");

            if (string.Equals(weather, latestSessionState.Weather, StringComparison.OrdinalIgnoreCase))
                preservedFields.Add("Weather");
            else
                changedFields.Add("Weather");

            if (string.Equals(timeOfDay, latestSessionState.TimeOfDay, StringComparison.OrdinalIgnoreCase))
                preservedFields.Add("TimeOfDay");
            else
                changedFields.Add("TimeOfDay");

            if (string.Equals(lighting, latestSessionState.Lighting, StringComparison.OrdinalIgnoreCase))
                preservedFields.Add("Lighting");
            else
                changedFields.Add("Lighting");
        }
        else
        {
            changedFields.AddRange(new[] { "Location", "Outfit", "Pose", "Action", "Weather", "TimeOfDay", "Lighting" });
        }

        // 10. Construct Resolved CharacterVisualState and SceneVisualState
        var characterVisualState = new CharacterVisualState(
            characterId: context.CharacterId,
            location: sceneLocation.Value,
            sceneRevision: targetRevision,
            outfit: outfit,
            hairstyle: hairstyle,
            appearanceOverrides: latestSessionState?.CharacterState.AppearanceOverrides,
            pose: pose,
            action: action,
            activeProps: props,
            validFromTurnId: intent.TurnId,
            sourceTurnId: intent.TurnId,
            version: (latestSessionState?.CharacterState.Version ?? 0) + 1
        );

        var sceneVisualState = new SceneVisualState(
            sessionId: intent.SessionId ?? Guid.Empty,
            characterId: context.CharacterId,
            location: sceneLocation.Value,
            characterState: characterVisualState,
            sceneRevision: targetRevision,
            sceneKey: sceneKey,
            timeOfDay: timeOfDay,
            weather: weather,
            lighting: lighting,
            atmosphere: atmosphere,
            props: props,
            persistentChanges: persistentChanges,
            validFromTurnId: intent.TurnId,
            sourceTurnId: intent.TurnId,
            version: (latestSessionState?.Version ?? 0) + 1
        );

        // 11. Build Provenance Record
        var provenance = new VisualStateProvenance(
            OutfitSource: outfitSource,
            HairstyleSource: hairSource,
            PoseSource: poseActionSource,
            ActionSource: poseActionSource,
            LocationSource: !string.IsNullOrWhiteSpace(intent.LocationHint) ? "CurrentIntent" : "PreviousSceneState",
            WeatherSource: envSource.Split('|').FirstOrDefault() ?? "Default",
            TimeOfDaySource: envSource.Split('|').ElementAtOrDefault(1) ?? "Default",
            LightingSource: envSource.Split('|').ElementAtOrDefault(2) ?? "Default",
            AtmosphereSource: atmosphereSource,
            PropsSource: propsSource,
            WorldMutationsSource: propsSource,
            TransitionType: transitionType,
            ResolvedAt: DateTime.UtcNow
        );

        // 12. Persist record to DB via reader if available
        if (_stateReader != null && intent.SessionId.HasValue && intent.SessionId.Value != Guid.Empty)
        {
            try
            {
                await _stateReader.SaveStateAsync(sceneVisualState, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VisualContinuityResolver] Non-fatal state persistence failure for SessionId={SessionId}", intent.SessionId);
            }
        }

        _logger.LogInformation(
            "[VisualContinuityResolver] Resolved visual continuity for CharacterId={CharacterId}, Location='{Location}', Transition={Transition}, Fingerprint={Fingerprint}, Changed={ChangedCount}, Preserved={PreservedCount}",
            context.CharacterId, sceneVisualState.Location, transitionType, sceneVisualState.Fingerprint, changedFields.Count, preservedFields.Count);

        return new VisualContinuityResult(
            SceneVisualState: sceneVisualState,
            TransitionType: transitionType,
            ChangedFields: changedFields,
            PreservedFields: preservedFields,
            InvalidatedFields: invalidatedFields,
            Provenance: provenance,
            SceneRevision: targetRevision,
            Fingerprint: sceneVisualState.Fingerprint
        );
    }
}
