using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class VisualContextResolver : IVisualContextResolver
{
    private const int MaxRelevantVisualMemories = 3;
    private readonly ILogger<VisualContextResolver> _logger;

    public VisualContextResolver(ILogger<VisualContextResolver> logger)
    {
        _logger = logger;
    }

    public Task<VisualContextResolutionResult> ResolveVisualContextAsync(
        Guid characterId,
        SceneSpecification scene,
        SceneCompositionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scene, nameof(scene));
        ArgumentNullException.ThrowIfNull(context, nameof(context));

        var profileVersion = context.CharacterVisualProfile?.VisualVersion ?? 1;

        // 1. Evaluate Transition Type
        var transitionType = SceneContinuityPolicy.EvaluateTransition(
            previousLocation: context.PreviousScene?.Location,
            currentLocation: scene.Location,
            previousAction: context.PreviousScene?.Action,
            currentAction: scene.Action
        );

        // 2. Canonical Identity Reference (Highest dominating authority)
        var canonicalRef = context.CanonicalVisualReference;

        // 3. Current Appearance (From Visual Profile)
        var currentAppearance = context.CharacterVisualProfile;

        // 4. Predecessor Visual Memory (Direct predecessor turn memory if available)
        var predecessorMemory = context.PreviousAcceptedVisualMemory;

        // 5. Bounded Relevant Older Memories Selection (Max 3)
        var candidates = context.RelevantVisualMemories ?? Array.Empty<CharacterVisualMemory>();

        var selectedMemories = candidates
            .Where(m => predecessorMemory == null || m.ArtifactId != predecessorMemory.ArtifactId)
            .OrderByDescending(m => !string.IsNullOrEmpty(m.Context) && m.Context.Contains(scene.Location, StringComparison.OrdinalIgnoreCase)) // Location similarity
            .ThenByDescending(m => m.IdentityScore ?? 0.5f) // High identity score
            .ThenByDescending(m => m.SceneRevision) // Recency
            .Take(MaxRelevantVisualMemories)
            .ToList();

        var summary = $"Resolved visual context for CharacterId={characterId}: Canonical='{(canonicalRef != null ? canonicalRef.ReferenceUrl : "None")}', ProfileVersion={profileVersion}, Predecessor='{(predecessorMemory != null ? predecessorMemory.ArtifactId.ToString() : "None")}', SelectedMemoriesCount={selectedMemories.Count}, Transition={transitionType}.";

        _logger.LogInformation("[VisualContextResolver] {Summary}", summary);

        var result = new VisualContextResolutionResult(
            CharacterId: characterId,
            VisualProfileVersion: profileVersion,
            CanonicalIdentityReference: canonicalRef,
            CurrentAppearance: currentAppearance,
            PredecessorVisualMemory: predecessorMemory,
            RelevantOlderMemories: selectedMemories,
            TransitionType: transitionType,
            SelectionSummary: summary
        );

        return Task.FromResult(result);
    }
}
