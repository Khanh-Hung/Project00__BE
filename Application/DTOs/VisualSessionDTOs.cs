namespace Application.DTOs;

/// <summary>
/// User-facing response representing the current active or historical visual artifact.
/// </summary>
public sealed record VisualArtifactResponse(
    Guid ArtifactId,
    Guid TurnId,
    Guid SessionId,
    string ImageUrl,
    bool IsCurrent,
    int VisualRevision,
    int SceneRevision,
    DateTime CreatedAt,
    string? Prompt = null,
    string? Model = null,
    string? LifecycleStatus = null,
    Guid? PredecessorArtifactId = null
);

/// <summary>
/// Status and execution progress for a visual generation job.
/// </summary>
public sealed record VisualGenerationStatusResponse(
    Guid JobId,
    Guid GenerationRequestId,
    Guid SessionId,
    Guid TurnId,
    string Status,
    int AttemptNumber,
    bool HasArtifact,
    string? ImageUrl,
    string? FailureReason,
    bool IsQuarantined,
    DateTime? CompletedAt,
    int? VisualRevision = null
);

/// <summary>
/// Individual entry in the session visual history list.
/// </summary>
public sealed record VisualHistoryEntry(
    Guid ArtifactId,
    Guid TurnId,
    Guid? GenerationJobId,
    int SceneRevision,
    int VisualRevision,
    bool IsCurrent,
    bool IsQuarantined,
    string LifecycleStatus,
    DateTime CreatedAt,
    string? ImageUrl,
    string? Prompt,
    Guid? PredecessorArtifactId
);

/// <summary>
/// Result of an artifact promotion operation.
/// </summary>
public sealed record ArtifactAcceptanceResult(
    bool Success,
    Guid? ArtifactId,
    int VisualRevision,
    string Status,
    string? Message = null
);

/// <summary>
/// Evaluation result of artifact cleanup eligibility.
/// </summary>
public sealed record ArtifactRetentionEvaluationResult(
    Guid ArtifactId,
    bool IsProtected,
    string ProtectionReason,
    bool IsEligibleForCleanup
);
