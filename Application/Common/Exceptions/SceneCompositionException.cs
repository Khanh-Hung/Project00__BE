using Domain.Enums;

namespace Application.Common.Exceptions;

/// <summary>
/// Authoritative exception thrown when scene composition or visual context resolution fails.
/// Carries a typed SceneCompositionFailureCategory to ensure explicit, fail-fast error propagation
/// without silent fallback to unconditioned or degraded generation.
/// </summary>
public sealed class SceneCompositionException : Exception
{
    public SceneCompositionFailureCategory FailureCategory { get; }

    public SceneCompositionException(SceneCompositionFailureCategory category, string message, Exception? innerException = null)
        : base($"[SceneCompositionFailure.{category}] {message}", innerException)
    {
        FailureCategory = category;
    }
}
