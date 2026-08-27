using Domain.Enums;

namespace Application.DTOs;

/// <summary>
/// Context passed into reference resolution before image generation.
/// </summary>
public sealed record VisualReferenceContext(
    int SceneRevision = 1,
    string? SceneType = null,
    string? Outfit = null,
    string? Pose = null,
    string? PreviousVisualState = null,
    int MaxSecondaryReferences = 3,
    int MaxSceneReferences = 2
);

/// <summary>
/// A resolved visual reference with scoring metadata.
/// </summary>
public sealed record ResolvedReference(
    Guid ReferenceId,
    string ReferenceUrl,
    VisualReferenceType Type,
    bool IsCanonical,
    int Priority,
    float Score,
    string SelectionReason
);

/// <summary>
/// Result set returned by ICharacterVisualReferenceResolver for generation conditioning.
/// </summary>
public sealed record VisualReferenceSet(
    Guid CharacterId,
    int VisualProfileVersion,
    ResolvedReference? PrimaryIdentityReference,
    IReadOnlyList<ResolvedReference> SecondaryReferences,
    IReadOnlyList<ResolvedReference> SceneReferences,
    string SelectionSummary
);

/// <summary>
/// Data transfer object for CharacterVisualProfile.
/// </summary>
public sealed record CharacterVisualProfileDto(
    Guid Id,
    Guid CharacterId,
    int VisualVersion,
    Guid? PrimaryReferenceId,
    Guid? FaceReferenceId,
    string? HairDescription,
    string? EyeDescription,
    string? SkinDescription,
    string? BodyDescription,
    string? DistinguishingFeatures,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

/// <summary>
/// Request to register a visual reference.
/// </summary>
public sealed record RegisterVisualReferenceRequest(
    Guid CharacterId,
    string ReferenceUrl,
    VisualReferenceType Type = VisualReferenceType.SecondaryCanonical,
    VisualReferenceStatus Status = VisualReferenceStatus.Active,
    bool IsCanonical = false,
    Guid? ArtifactId = null,
    int Priority = 0,
    Guid? SourceGenerationJobId = null,
    int SourceVisualRevision = 0
);
