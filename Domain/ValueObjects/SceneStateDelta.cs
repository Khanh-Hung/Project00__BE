namespace Domain.ValueObjects;

public sealed record SceneStateDelta(
    string? LocationChange = null,
    string? OutfitChange = null,
    string? TimeOfDayChange = null,
    string? PoseChange = null,
    string? HeldItemsChange = null,
    string? AtmosphereChange = null,
    string? Evidence = null
);
