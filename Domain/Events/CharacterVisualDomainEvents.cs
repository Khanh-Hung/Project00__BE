using Domain.Enums;

namespace Domain.Events;

public sealed record CharacterVisualProfileCreated(
    Guid CharacterId,
    Guid ProfileId,
    int VisualVersion,
    DateTime OccurredAt
);

public sealed record CharacterVisualProfileUpdated(
    Guid CharacterId,
    Guid ProfileId,
    int NewVisualVersion,
    DateTime OccurredAt
);

public sealed record VisualReferenceRegistered(
    Guid CharacterId,
    Guid ReferenceId,
    VisualReferenceType Type,
    bool IsCanonical,
    DateTime OccurredAt
);

public sealed record VisualReferencePromoted(
    Guid CharacterId,
    Guid ReferenceId,
    Guid? PreviousCanonicalReferenceId,
    int NewVisualVersion,
    DateTime OccurredAt
);

public sealed record VisualReferenceArchived(
    Guid CharacterId,
    Guid ReferenceId,
    DateTime OccurredAt
);

public sealed record VisualEvidenceRecorded(
    Guid CharacterId,
    Guid ArtifactId,
    int VisualProfileVersion,
    int SceneRevision,
    DateTime OccurredAt
);
