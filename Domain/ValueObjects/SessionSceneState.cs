namespace Domain.ValueObjects;

public sealed record SessionSceneState(
    string? CurrentLocation = null,
    string? CurrentOutfit = null,
    string? CurrentTimeOfDay = null,
    string? CurrentPose = null,
    string? HeldItems = null,
    string? Atmosphere = null,
    DateTime? LastUpdatedAt = null
)
{
    /// <summary>
    /// Implements the Visual Continuity Invariant: "Nothing changes unless explicitly changed."
    /// NewState = OldState ⊕ Delta
    /// </summary>
    public SessionSceneState ApplyDelta(SceneStateDelta delta)
    {
        if (delta == null) return this;

        string? resolvedHeldItems = this.HeldItems;
        if (!string.IsNullOrWhiteSpace(delta.HeldItemsChange))
        {
            var item = delta.HeldItemsChange.Trim();
            if (item.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                item.Equals("empty", StringComparison.OrdinalIgnoreCase) ||
                item.Equals("cleared", StringComparison.OrdinalIgnoreCase) ||
                item.Equals("dropped", StringComparison.OrdinalIgnoreCase) ||
                item.Equals("placed_down", StringComparison.OrdinalIgnoreCase))
            {
                resolvedHeldItems = null;
            }
            else
            {
                resolvedHeldItems = item;
            }
        }

        return new SessionSceneState(
            CurrentLocation: !string.IsNullOrWhiteSpace(delta.LocationChange) ? delta.LocationChange.Trim() : this.CurrentLocation,
            CurrentOutfit: !string.IsNullOrWhiteSpace(delta.OutfitChange) ? delta.OutfitChange.Trim() : this.CurrentOutfit,
            CurrentTimeOfDay: !string.IsNullOrWhiteSpace(delta.TimeOfDayChange) ? delta.TimeOfDayChange.Trim() : this.CurrentTimeOfDay,
            CurrentPose: !string.IsNullOrWhiteSpace(delta.PoseChange) ? delta.PoseChange.Trim() : this.CurrentPose,
            HeldItems: resolvedHeldItems,
            Atmosphere: !string.IsNullOrWhiteSpace(delta.AtmosphereChange) ? delta.AtmosphereChange.Trim() : this.Atmosphere,
            LastUpdatedAt: DateTime.UtcNow
        );
    }
}
