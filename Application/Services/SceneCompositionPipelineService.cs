using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class SceneCompositionPipelineService : ISceneCompositionPipelineService
{
    private readonly ISceneCompositionContextFactory _contextFactory;
    private readonly ISceneComposer _sceneComposer;
    private readonly IVisualContextResolver _visualContextResolver;
    private readonly IScenePromptComposer _promptComposer;
    private readonly SceneGenerationRequestMapper _requestMapper;
    private readonly ILogger<SceneCompositionPipelineService> _logger;

    public SceneCompositionPipelineService(
        ISceneCompositionContextFactory contextFactory,
        ISceneComposer sceneComposer,
        IVisualContextResolver visualContextResolver,
        IScenePromptComposer promptComposer,
        SceneGenerationRequestMapper requestMapper,
        ILogger<SceneCompositionPipelineService> logger)
    {
        _contextFactory = contextFactory;
        _sceneComposer = sceneComposer;
        _visualContextResolver = visualContextResolver;
        _promptComposer = promptComposer;
        _requestMapper = requestMapper;
        _logger = logger;
    }

    public async Task<SceneCompositionPipelineResult> ExecuteAsync(
        SceneIntent intent,
        GenerationProfile generationProfile,
        int sceneRevision = 1,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent, nameof(intent));
        ArgumentNullException.ThrowIfNull(generationProfile, nameof(generationProfile));

        // 1. Build SceneCompositionContext via Factory (Queries Profile, Canonical, Lifecycle-Valid Memories, Previous Scene)
        var context = await _contextFactory.CreateContextAsync(
            characterId: intent.CharacterId,
            sessionId: intent.SessionId,
            turnId: intent.TurnId,
            sceneRevision: sceneRevision,
            locationContext: intent.LocationHint,
            ct: ct);

        // 2. Compose Normalized SceneSpecification
        var sceneSpec = await _sceneComposer.ComposeAsync(intent, context, ct);

        // 3. Resolve Prioritized Visual Context (Canonical > Appearance > Predecessor > Bounded Memories <= 3)
        var visualContext = await _visualContextResolver.ResolveVisualContextAsync(intent.CharacterId, sceneSpec, context, ct);

        // 4. Compile Deterministic Structured ScenePrompt
        var prompt = _promptComposer.ComposePrompt(sceneSpec, visualContext);

        // 5. Map to Engine-Compatible VisualSnapshot
        var snapshot = _requestMapper.MapToVisualSnapshot(sceneSpec, visualContext, generationProfile, _promptComposer);

        _logger.LogInformation(
            "[SceneCompositionPipelineService] Executed pipeline for CharacterId={CharacterId}, SceneId={SceneId}, Fingerprint={Fingerprint}, Revision={Revision}",
            intent.CharacterId, sceneSpec.Id, sceneSpec.SceneFingerprint, sceneRevision);

        return new SceneCompositionPipelineResult(
            SceneSpecification: sceneSpec,
            VisualContext: visualContext,
            ScenePrompt: prompt,
            VisualSnapshot: snapshot
        );
    }
}
