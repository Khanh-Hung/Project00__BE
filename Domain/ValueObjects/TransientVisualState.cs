namespace Domain.ValueObjects;

/// <summary>
/// Transient Frame State represents dynamic momentary physical action, pose, expression, and gaze of the turn.
/// These momentary actions evolve each frame without directly mutating permanent anatomical DNA or persistent clothing/location state.
/// </summary>
public sealed record TransientVisualState(
    string? Pose = null,
    string? Action = null,
    string? Expression = null,
    string? Gaze = null,
    string? Gesture = null,
    string? Interaction = null
);
