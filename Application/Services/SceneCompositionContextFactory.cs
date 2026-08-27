using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Policies;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class SceneCompositionContextFactory : ISceneCompositionContextFactory
{
    private readonly ICharacterVisualProfileReader _profileReader;
    private readonly ICanonicalReferenceReader _canonicalReader;
    private readonly IVisualMemoryReader _memoryReader;
    private readonly IPreviousSceneReader _previousSceneReader;
    private readonly ILogger<SceneCompositionContextFactory> _logger;

    public SceneCompositionContextFactory(
        ICharacterVisualProfileReader profileReader,
        ICanonicalReferenceReader canonicalReader,
        IVisualMemoryReader memoryReader,
        IPreviousSceneReader previousSceneReader,
        ILogger<SceneCompositionContextFactory> logger)
    {
        _profileReader = profileReader;
        _canonicalReader = canonicalReader;
        _memoryReader = memoryReader;
        _previousSceneReader = previousSceneReader;
        _logger = logger;
    }

    public async Task<SceneCompositionContext> CreateContextAsync(
        Guid characterId,
        Guid? sessionId = null,
        Guid? turnId = null,
        int sceneRevision = 1,
        string? locationContext = null,
        CancellationToken ct = default)
    {
        var profileTask = _profileReader.GetProfileByCharacterIdAsync(characterId, ct);
        var canonicalTask = _canonicalReader.GetActiveCanonicalReferenceAsync(characterId, ct);
        var memoriesTask = _memoryReader.GetRelevantMemoriesAsync(characterId, locationContext, maxResults: 3, ct);
        var latestMemoryTask = _memoryReader.GetLatestMemoryAsync(characterId, ct);
        var prevSceneTask = sessionId.HasValue
            ? _previousSceneReader.GetLatestSceneBySessionAsync(sessionId.Value, ct)
            : Task.FromResult<SceneSpecification?>(null);

        await Task.WhenAll(profileTask, canonicalTask, memoriesTask, latestMemoryTask, prevSceneTask);

        var profile = await profileTask;
        var canonical = await canonicalTask;
        var memories = await memoriesTask;
        var predecessorMemory = await latestMemoryTask;
        var previousScene = await prevSceneTask;

        var transitionType = SceneContinuityPolicy.EvaluateTransition(
            previousLocation: previousScene?.Location,
            currentLocation: locationContext ?? previousScene?.Location ?? "Unknown",
            previousAction: previousScene?.Action,
            currentAction: null
        );

        _logger.LogInformation(
            "[SceneCompositionContextFactory] Created context for CharacterId={CharacterId}, SessionId={SessionId}, TurnId={TurnId}, Canonical={HasCanonical}, MemoriesCount={MemoriesCount}",
            characterId, sessionId, turnId, canonical != null, memories.Count);

        return new SceneCompositionContext(
            CharacterId: characterId,
            SessionId: sessionId,
            TurnId: turnId,
            SceneRevision: sceneRevision,
            PreviousScene: previousScene,
            PreviousAcceptedVisualMemory: predecessorMemory,
            CharacterVisualProfile: profile,
            CanonicalVisualReference: canonical,
            RelevantVisualMemories: memories,
            TransitionType: transitionType
        );
    }
}
