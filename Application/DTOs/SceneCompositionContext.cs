using Domain.Entities;
using Domain.Enums;

namespace Application.DTOs;

/// <summary>
/// Context provided to the Scene Composer to ground new scenes with continuity and character identity.
/// </summary>
public sealed record SceneCompositionContext(
    Guid CharacterId,
    Guid? SessionId = null,
    Guid? TurnId = null,
    int SceneRevision = 1,
    SceneSpecification? PreviousScene = null,
    CharacterVisualMemory? PreviousAcceptedVisualMemory = null,
    CharacterVisualProfile? CharacterVisualProfile = null,
    CharacterVisualReference? CanonicalVisualReference = null,
    IReadOnlyList<CharacterVisualMemory>? RelevantVisualMemories = null,
    SceneTransitionType TransitionType = SceneTransitionType.LocationTransition
);

/// <summary>
/// Resolved visual context holding prioritized references and memories for prompt composition and conditioning.
/// </summary>
public sealed record VisualContextResolutionResult(
    Guid CharacterId,
    int VisualProfileVersion,
    CharacterVisualReference? CanonicalIdentityReference,
    CharacterVisualProfile? CurrentAppearance,
    CharacterVisualMemory? PredecessorVisualMemory,
    IReadOnlyList<CharacterVisualMemory> RelevantOlderMemories,
    SceneTransitionType TransitionType,
    string SelectionSummary
);

/// <summary>
/// Structured prompt compilation output.
/// </summary>
public sealed record ScenePrompt(
    string PositivePrompt,
    string NegativePrompt,
    string StructuredSummary
);
