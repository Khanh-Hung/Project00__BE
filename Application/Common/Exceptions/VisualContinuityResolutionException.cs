using Domain.Enums;

namespace Application.Common.Exceptions;

/// <summary>
/// Authoritative exception thrown when Visual Continuity or Scene Evolution resolution fails.
/// Enforces fail-fast reliability invariant: prevents silent degraded generation fallback.
/// </summary>
public sealed class VisualContinuityResolutionException : Exception
{
    public SceneCompositionFailureCategory FailureCategory { get; }
    public Guid? SessionId { get; }
    public Guid? TurnId { get; }
    public int? SceneRevision { get; }

    public VisualContinuityResolutionException(
        SceneCompositionFailureCategory failureCategory,
        string message,
        Guid? sessionId = null,
        Guid? turnId = null,
        int? sceneRevision = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCategory = failureCategory;
        SessionId = sessionId;
        TurnId = turnId;
        SceneRevision = sceneRevision;
    }
}
