namespace Domain.ValueObjects;

/// <summary>
/// Transient Frame State represents dynamic momentary physical action, pose, expression, and gaze of the turn.
/// These momentary actions evolve each frame without mutating permanent anatomical DNA or persistent clothing/location state.
/// </summary>
public sealed record TransientVisualState(
    string? Pose = null,
    string? Action = null,
    string? Expression = null,
    string? Gaze = null,
    string? Gesture = null,
    string? Interaction = null
)
{
    /// <summary>
    /// Maps evidence-based delta fields directly into the transient visual state of the turn.
    /// </summary>
    public static TransientVisualState FromDelta(
        SceneStateDelta? delta,
        string? defaultPose = null,
        string? defaultExpression = null)
    {
        if (delta == null)
        {
            return new TransientVisualState(
                Pose: defaultPose,
                Expression: defaultExpression
            );
        }

        return new TransientVisualState(
            Pose: !string.IsNullOrWhiteSpace(delta.PoseChange) ? delta.PoseChange.Trim() : defaultPose,
            Action: !string.IsNullOrWhiteSpace(delta.ActionChange) ? delta.ActionChange.Trim() : null,
            Expression: !string.IsNullOrWhiteSpace(delta.ExpressionChange) ? delta.ExpressionChange.Trim() : defaultExpression,
            Gaze: null,
            Gesture: null,
            Interaction: null
        );
    }
}
