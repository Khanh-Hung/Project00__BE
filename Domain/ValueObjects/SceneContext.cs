namespace Domain.ValueObjects;

public sealed record SceneContext(
    string? Location = null,
    string? TimeOfDay = null,
    string? Outfit = null,
    string? Pose = null,
    string? Expression = null,
    string? Action = null
);
