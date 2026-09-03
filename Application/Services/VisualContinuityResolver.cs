using Application.Common.Exceptions;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects.Scene;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class VisualContinuityResolver : IVisualContinuityResolver
{
    private readonly ISceneVisualStateReader _stateReader;
    private readonly ILogger<VisualContinuityResolver> _logger;

    public VisualContinuityResolver(
        ISceneVisualStateReader stateReader,
        ILogger<VisualContinuityResolver> logger)
    {
        _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        // 1. Authoritative DB Query: Query State Reader for active session state & historical re-entry state
        SceneVisualState? latestSessionState;
        SceneVisualState? reenteredHistoricalState;

        try
        {
            if (intent.SessionId.HasValue && intent.SessionId.Value != Guid.Empty)
            {
                latestSessionState = await _stateReader.GetLatestBySessionAsync(intent.SessionId.Value, ct);
                reenteredHistoricalState = await _stateReader.GetLatestBySessionAndSceneKeyAsync(intent.SessionId.Value, sceneKey, ct);
            }
            else
            {
                latestSessionState = null;
                reenteredHistoricalState = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VisualContinuityResolver] Authoritative DB read failed for SessionId={SessionId}. Failing fast.", intent.SessionId);
            throw new VisualContinuityResolutionException(
                failureCategory: SceneCompositionFailureCategory.ContextResolutionFailure,
                message: $"Failed to read authoritative visual continuity state for session '{intent.SessionId}': {ex.Message}",
                sessionId: intent.SessionId,
                turnId: intent.TurnId,
                sceneRevision: targetRevision,
                innerException: ex
            );
        }

        // 2. Synthesize previous state from Context.PreviousScene ONLY IF no DB record was found (cold start / first revision)
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
                sourceTurnId: context.TurnId,
                validFromRevision: context.PreviousScene.SceneRevision
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
                sourceTurnId: context.TurnId,
                validFromRevision: context.PreviousScene.SceneRevision
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

        // 4. Resolve Character Outfit (CurrentIntent > PreviousSceneState > ActiveVisualMemory.Outfit > ProfileDefault)
        var activeValidMemory = context.RelevantVisualMemories?.FirstOrDefault(m => m.IsActiveForRevision(targetRevision))
                                ?? (context.PreviousAcceptedVisualMemory?.IsActiveForRevision(targetRevision) == true ? context.PreviousAcceptedVisualMemory : null);

        var (outfit, outfitSource) = VisualContinuityPolicy.ResolveOutfit(
            intentOutfit: intent.OutfitHint,
            previousSceneOutfit: latestSessionState?.CharacterState.Outfit ?? context.PreviousScene?.OutfitContext,
            activeMemoryOutfit: activeValidMemory?.Outfit,
            profileDefaultOutfit: context.CharacterVisualProfile?.CurrentOutfit
        );

        // 5. Resolve Character Hairstyle (CurrentIntent > PreviousSceneState > ActiveVisualMemory.Hairstyle > ProfileDefault)
        var (hairstyle, hairSource) = VisualContinuityPolicy.ResolveHairstyle(
            intentHairstyle: intent.HairstyleHint,
            previousSceneHairstyle: latestSessionState?.CharacterState.Hairstyle ?? context.CharacterVisualProfile?.Hairstyle,
            activeMemoryHairstyle: activeValidMemory?.Hairstyle,
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

            if (string.Equals(hairstyle, latestSessionState.CharacterState.Hairstyle, StringComparison.OrdinalIgnoreCase))
                preservedFields.Add("Hairstyle");
            else
            {
                changedFields.Add("Hairstyle");
                invalidatedFields.Add("PreviousHairstyle");
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
            changedFields.AddRange(new[] { "Location", "Outfit", "Hairstyle", "Pose", "Action", "Weather", "TimeOfDay", "Lighting" });
        }

        // 10. Construct Resolved CharacterVisualState and SceneVisualState
        uint currentVersion = reenteredHistoricalState?.Version 
            ?? (string.Equals(latestSessionState?.SceneKey, sceneKey, StringComparison.OrdinalIgnoreCase) ? (latestSessionState?.Version ?? 0) : 0);
        uint nextVersion = currentVersion + 1;

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
            validFromRevision: targetRevision,
            version: nextVersion
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
            validFromRevision: targetRevision,
            version: nextVersion
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

        // 12. Authoritative Persistence with CAS Concurrency Check
        if (intent.SessionId.HasValue && intent.SessionId.Value != Guid.Empty)
        {
            try
            {
                await _stateReader.SaveStateAsync(sceneVisualState, expectedVersion: currentVersion, ct: ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning("[VisualContinuityResolver] Concurrency conflict persisting state for SessionId={SessionId}, SceneKey={SceneKey}",
                    intent.SessionId, sceneKey);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VisualContinuityResolver] Authoritative persistence failed for SessionId={SessionId}. Failing fast.", intent.SessionId);
                throw new VisualContinuityResolutionException(
                    failureCategory: SceneCompositionFailureCategory.ContextResolutionFailure,
                    message: $"Failed to persist authoritative visual continuity state for session '{intent.SessionId}': {ex.Message}",
                    sessionId: intent.SessionId,
                    turnId: intent.TurnId,
                    sceneRevision: targetRevision,
                    innerException: ex
                );
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
