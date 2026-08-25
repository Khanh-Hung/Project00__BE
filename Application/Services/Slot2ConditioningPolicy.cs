namespace Application.Services;

/// <summary>
/// Configurable policy abstraction for Slot 2 (Previous Scene) conditioning parameters.
/// Allows fine-tuned control over continuity versus action compliance without hardcoded magic numbers.
/// </summary>
public sealed record Slot2ConditioningPolicy(
    double SameSceneWeight = 0.15,
    double SameSceneEndAt = 0.30,
    double TransitionWeight = 0.08,
    double TransitionEndAt = 0.20,
    bool BypassOnColdStart = true
)
{
    public static readonly Slot2ConditioningPolicy Default = new();

    public (double Weight, double EndAt, bool IsActive) Resolve(bool isColdStart, bool isTransition)
    {
        if (isColdStart && BypassOnColdStart)
        {
            return (0.0, 0.0, false);
        }

        if (isTransition)
        {
            return (TransitionWeight, TransitionEndAt, true);
        }

        return (SameSceneWeight, SameSceneEndAt, true);
    }
}
