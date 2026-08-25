using Application.Abstractions.Data;
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
    private readonly ILogger<VisualStateResolver> _logger;

    public VisualStateResolver(
        IUnitOfWork unitOfWork,
        ISceneStateTrackerService? sceneStateTracker,
        IVisualGenerationProfileProvider? profileProvider,
        ILogger<VisualStateResolver>? logger = null)
    {
        _unitOfWork = unitOfWork;
        _sceneStateTracker = sceneStateTracker;
        _profileProvider = profileProvider ?? new VisualGenerationProfileProvider();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VisualStateResolver>.Instance;
    }

    public VisualStateResolver(
        IUnitOfWork unitOfWork,
        ISceneStateTrackerService? sceneStateTracker,
        ILogger<VisualStateResolver>? logger = null)
        : this(unitOfWork, sceneStateTracker, new VisualGenerationProfileProvider(), logger)
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

        var generationProfile = _profileProvider.ResolveProfile(character);

        var snapshot = VisualSnapshot.Create(
            turnId: turnId,
            sessionId: session.Id,
            characterId: character.Id,
            sceneRevision: targetRevision,
            visualIdentity: character.VisualIdentity,
            sceneState: updatedSceneState,
            transientState: transientState,
            generationProfile: generationProfile,
            previousSceneImageUrl: null,
            predecessorSceneRevision: targetRevision > 1 ? targetRevision - 1 : null,
            fallbackReferenceUrl: character.AvatarUrl,
            sceneDescription: delta.SceneDescription
        );

        return (updatedSceneState, transientState, snapshot);
    }
}
