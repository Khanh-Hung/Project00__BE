using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;

namespace Tests.Helpers;

public sealed class FakeSceneCompositionPipelineService : ISceneCompositionPipelineService
{
    private readonly bool _shouldFail;

    public FakeSceneCompositionPipelineService(bool shouldFail = false)
    {
        _shouldFail = shouldFail;
    }

    public Task<SceneCompositionPipelineResult> ExecuteAsync(
        SceneIntent intent,
        GenerationProfile generationProfile,
        int sceneRevision = 1,
        CancellationToken ct = default)
    {
        if (_shouldFail)
        {
            throw new InvalidOperationException("ComfyUI service unavailable.");
        }

        var sceneSpec = new SceneSpecification(
            characterId: intent.CharacterId,
            location: intent.LocationHint,
            action: intent.ActionHint,
            sceneRevision: sceneRevision,
            sessionId: intent.SessionId,
            turnId: intent.TurnId
        );

        var prompt = new ScenePrompt("seraphina, elegant celestial gown", "low quality", "structured summary");
        var snapshot = new VisualSnapshot(
            TurnId: intent.TurnId ?? Guid.NewGuid(),
            SessionId: intent.SessionId ?? Guid.NewGuid(),
            CharacterId: intent.CharacterId,
            SceneRevision: sceneRevision,
            VisualIdentity: null,
            SceneState: new SessionSceneState(intent.LocationHint, "Clear", "Day", "Neutral"),
            TransientState: null,
            GenerationProfile: generationProfile
        );

        return Task.FromResult(new SceneCompositionPipelineResult(sceneSpec, null!, prompt, snapshot));
    }
}
