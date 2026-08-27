using Domain.Enums;

namespace Application.DTOs;

public sealed record VisualReferenceContext(
    int SceneRevision = 1,
    int MaxSecondaryReferences = 2,
    int MaxSceneReferences = 2,
    string? RequiredTag = null
);

public sealed record ResolvedReference(
    Guid ReferenceId,
    string ReferenceUrl,
    VisualReferenceType Type,
    bool IsCanonical,
    int Priority,
    float Score,
    string SelectionReason
);

public sealed record VisualReferenceSet(
    Guid CharacterId,
    int VisualProfileVersion,
    ResolvedReference? PrimaryIdentityReference,
    IReadOnlyList<ResolvedReference> SecondaryReferences,
    IReadOnlyList<ResolvedReference> SceneReferences,
    string SelectionSummary
);

public sealed record CharacterVisualProfileDto(
    Guid CharacterId,
    int VisualVersion,
    Guid? PrimaryReferenceId,
    Guid? FaceReferenceId,
    string? EyeColor,
    string? HairColor,
    string? SkinTone,
    string? FacialFeatures,
    string? PermanentMarks,
    string? BodyIdentity,
    string? Hairstyle,
    string? CurrentOutfit,
    string? Makeup,
    string? Accessories,
    string? TemporaryAppearance,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public sealed record RegisterVisualReferenceRequest(
    Guid CharacterId,
    string ReferenceUrl,
    bool IsCanonical = false,
    VisualReferenceType Type = VisualReferenceType.SecondaryCanonical,
    VisualReferenceStatus Status = VisualReferenceStatus.Active,
    Guid? ArtifactId = null,
    int Priority = 0,
    Guid? SourceGenerationJobId = null,
    int SourceVisualRevision = 0
);
