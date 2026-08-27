using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable diagnostic evaluation result of a session's visual state integrity.
/// Contains authoritative status, session identifier, current artifact pointer, expected artifact pointer, and diagnostic reason.
/// </summary>
public sealed record ArtifactConsistencyResult(
    VisualStateConsistencyStatus Status,
    Guid SessionId,
    Guid? CurrentArtifactId,
    Guid? ExpectedArtifactId,
    string? Reason
);
