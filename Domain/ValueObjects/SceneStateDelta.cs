namespace Domain.ValueObjects;

/// <summary>
/// Evidence-based delta representing explicit modifications in the current dialogue turn.
/// Omitted/null fields indicate no change (KEEP).
/// </summary>
public sealed record SceneStateDelta(
    string? LocationChange = null,
    string? PositionChange = null,
    string? OutfitChange = null,
    string? TimeOfDayChange = null,
    string? PoseChange = null,
    string? ActionChange = null,
    string? ExpressionChange = null,
    string? HeldItemsChange = null,
    string? AtmosphereChange = null,
    string? Evidence = null
);
