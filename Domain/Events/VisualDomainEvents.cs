using Domain.Enums;

namespace Domain.Events;

/// <summary>
/// Domain event published when a visual artifact is accepted and becomes current.
/// </summary>
public sealed record VisualArtifactAccepted(
    Guid SessionId,
    Guid TurnId,
    Guid ArtifactId,
    Guid GenerationJobId,
    int VisualRevision,
    DateTime OccurredAt,
    Guid? AcceptedAttemptId = null
);

/// <summary>
/// Domain event published when a previous active visual artifact is superseded by a newer accepted artifact.
/// </summary>
public sealed record VisualArtifactSuperseded(
    Guid SessionId,
    Guid TurnId,
    Guid PreviousArtifactId,
    Guid NewArtifactId,
    int NewVisualRevision,
    DateTime OccurredAt
);

/// <summary>
/// Domain event published when async scene image generation is requested for a turn.
/// </summary>
public sealed record VisualGenerationRequested(
    Guid SessionId,
    Guid TurnId,
    Guid GenerationJobId,
    int SceneRevision,
    DateTime OccurredAt
);

/// <summary>
/// Domain event published when scene image regeneration is requested for a turn.
/// </summary>
public sealed record VisualGenerationRegenerated(
    Guid SessionId,
    Guid TurnId,
    Guid PreviousJobId,
    Guid NewJobId,
    int SceneRevision,
    DateTime OccurredAt
);

/// <summary>
/// Domain event published when visual state consistency verification detects an anomaly or evaluation result.
/// </summary>
public sealed record VisualStateConsistencyDetected(
    Guid SessionId,
    Guid TurnId,
    int VisualRevision,
    Guid? ArtifactId,
    VisualStateConsistencyStatus ConsistencyStatus,
    string? Reason,
    DateTime DetectedAt
);
