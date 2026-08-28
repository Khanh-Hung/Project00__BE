using Application.Abstractions.Data;
using Application.Common.Exceptions;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class VisualStateResolver : IVisualStateResolver
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISceneStateTrackerService? _sceneStateTracker;
    private readonly IVisualGenerationProfileProvider _profileProvider;
    private readonly ISceneCompositionPipelineService _sceneCompositionPipeline;
    private readonly ILogger<VisualStateResolver> _logger;

    public VisualStateResolver(
        IUnitOfWork unitOfWork,
        ISceneStateTrackerService? sceneStateTracker,
        IVisualGenerationProfileProvider? profileProvider,
        ISceneCompositionPipelineService sceneCompositionPipeline,
        ILogger<VisualStateResolver>? logger = null)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _sceneStateTracker = sceneStateTracker;
        _profileProvider = profileProvider ?? new VisualGenerationProfileProvider();
        _sceneCompositionPipeline = sceneCompositionPipeline ?? throw new ArgumentNullException(nameof(sceneCompositionPipeline));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VisualStateResolver>.Instance;
    }

    public VisualStateResolver(
        IUnitOfWork unitOfWork,
        ISceneStateTrackerService? sceneStateTracker,
        ISceneCompositionPipelineService sceneCompositionPipeline,
        ILogger<VisualStateResolver>? logger = null)
        : this(unitOfWork, sceneStateTracker, new VisualGenerationProfileProvider(), sceneCompositionPipeline, logger)
    {
    }

    public async Task<(SessionSceneState SceneState, TransientVisualState TransientState, VisualSnapshot Snapshot)> ResolveTurnVisualStateAsync(
        Character character,
        ChatSession session,
        string userMessage,
        string assistantReply,
        CharacterMood currentMood,
        Guid turnId,
        CancellationToken ct = default)
    {
        var oldState = session.SceneState ?? new SessionSceneState(
            CurrentLocation: character.WorldDescription ?? character.WorldName ?? character.Title ?? "Sanctuary",
            CurrentPosition: "Central Area",
            CurrentOutfit: character.VisualIdentity?.ClothingStyle ?? "Canonical Attire",
            CurrentTimeOfDay: "Daytime",
            HeldItems: null,
            Atmosphere: "Peaceful",
            SceneRevision: 0,
            LastUpdatedAt: Clock.Now
        );

        int targetRevision = (session.SceneState?.SceneRevision ?? 0) + 1;
        SceneStateDelta delta = new SceneStateDelta();

        if (_sceneStateTracker != null)
        {
            try
            {
                delta = await _sceneStateTracker.TrackAndExtractDeltaAsync(
                    character,
                    session.SceneState,
                    userMessage,
                    assistantReply,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to track dynamic scene state during turn {TurnId}.", turnId);
            }
        }

        var updatedSceneState = oldState.ApplyDelta(delta, explicitRevision: targetRevision);
        session.UpdateSceneState(updatedSceneState);

        var transientState = TransientVisualState.FromDelta(
            delta,
            defaultPose: "Graceful posture",
            defaultExpression: currentMood.ToString()
        );

        bool isColdStart = targetRevision <= 1;
        bool isTransition = !string.IsNullOrWhiteSpace(delta.LocationChange) && !string.Equals(delta.LocationChange, oldState.CurrentLocation, StringComparison.OrdinalIgnoreCase);

        var generationProfile = _profileProvider.ResolveProfile(
            character: character,
            workflowOverride: null,
            isTransition: isTransition,
            isColdStart: isColdStart
        );

        // Authoritative Scene Composition Pipeline Execution
        try
        {
            var intentLocation = !string.IsNullOrWhiteSpace(delta.LocationChange)
                ? delta.LocationChange
                : (updatedSceneState.CurrentLocation ?? character.WorldDescription ?? "Sanctuary");

            var intentAction = !string.IsNullOrWhiteSpace(delta.ActionChange)
                ? delta.ActionChange
                : (!string.IsNullOrWhiteSpace(userMessage) ? userMessage : assistantReply);

            var sceneIntent = new SceneIntent(
                characterId: character.Id,
                locationHint: intentLocation,
                actionHint: intentAction,
                poseHint: delta.PoseChange,
                environmentHint: delta.AtmosphereChange ?? updatedSceneState.Atmosphere,
                weatherHint: null,
                timeOfDayHint: delta.TimeOfDayChange ?? updatedSceneState.CurrentTimeOfDay,
                moodHint: currentMood.ToString(),
                outfitHint: delta.OutfitChange ?? updatedSceneState.CurrentOutfit,
                objectHints: !string.IsNullOrWhiteSpace(updatedSceneState.HeldItems) ? new[] { updatedSceneState.HeldItems } : null,
                sessionId: session.Id,
                turnId: turnId
            );

            var pipelineResult = await _sceneCompositionPipeline.ExecuteAsync(
                sceneIntent,
                generationProfile,
                targetRevision,
                ct);

            if (pipelineResult?.SceneSpecification == null || pipelineResult.VisualSnapshot == null)
            {
                throw new SceneCompositionException(
                    SceneCompositionFailureCategory.ContextResolutionFailure,
                    $"Scene composition pipeline returned incomplete result for TurnId '{turnId}'.");
            }

            var specRepo = _unitOfWork.GetRepository<SceneSpecification>();
            await specRepo.AddAsync(pipelineResult.SceneSpecification, ct);

            return (updatedSceneState, transientState, pipelineResult.VisualSnapshot);
        }
        catch (SceneCompositionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authoritative SceneCompositionPipeline failed for turn {TurnId}.", turnId);
            throw new SceneCompositionException(
                SceneCompositionFailureCategory.ContextResolutionFailure,
                $"Scene composition failed for TurnId '{turnId}': {ex.Message}",
                ex);
        }
    }
}
