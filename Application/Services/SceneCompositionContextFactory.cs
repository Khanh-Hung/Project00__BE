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
        _profileReader = profileReader ?? throw new ArgumentNullException(nameof(profileReader));
        _canonicalReader = canonicalReader ?? throw new ArgumentNullException(nameof(canonicalReader));
        _memoryReader = memoryReader ?? throw new ArgumentNullException(nameof(memoryReader));
        _previousSceneReader = previousSceneReader ?? throw new ArgumentNullException(nameof(previousSceneReader));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SceneCompositionContextFactory>.Instance;
    }

    public async Task<SceneCompositionContext> CreateContextAsync(
        Guid characterId,
        Guid? sessionId = null,
        Guid? turnId = null,
        int sceneRevision = 1,
        string? locationContext = null,
        CancellationToken ct = default)
    {
        // Execute sequential queries to preserve DbContext thread-safety under shared scoped lifetimes
        var profile = await _profileReader.GetProfileByCharacterIdAsync(characterId, ct);
        var canonical = await _canonicalReader.GetActiveCanonicalReferenceAsync(characterId, ct);
        var memories = await _memoryReader.GetRelevantMemoriesAsync(characterId, locationContext, maxResults: 3, ct);
        var predecessorMemory = await _memoryReader.GetLatestMemoryAsync(characterId, ct);
        var previousScene = sessionId.HasValue
            ? await _previousSceneReader.GetLatestSceneBySessionAsync(sessionId.Value, ct)
            : null;

        var transitionType = SceneContinuityPolicy.EvaluateTransition(
            previousLocation: previousScene?.Location,
            currentLocation: locationContext ?? previousScene?.Location ?? "Unknown",
            previousAction: previousScene?.Action,
            currentAction: null
        );

        _logger.LogInformation(
            "[SceneCompositionContextFactory] Created context for CharacterId={CharacterId}, SessionId={SessionId}, TurnId={TurnId}, Canonical={HasCanonical}, MemoriesCount={MemoriesCount}",
            characterId, sessionId, turnId, canonical != null, memories?.Count ?? 0);

        return new SceneCompositionContext(
            CharacterId: characterId,
            SessionId: sessionId,
            TurnId: turnId,
            SceneRevision: sceneRevision,
            PreviousScene: previousScene,
            PreviousAcceptedVisualMemory: predecessorMemory,
            CharacterVisualProfile: profile,
            CanonicalVisualReference: canonical,
            RelevantVisualMemories: memories ?? (IReadOnlyList<CharacterVisualMemory>)Array.Empty<CharacterVisualMemory>(),
            TransitionType: transitionType
        );
    }
}
