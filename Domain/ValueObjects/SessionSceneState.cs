namespace Domain.ValueObjects;

/// <summary>
/// Persistent Scene State represents the durable spatial, environmental, and clothing context.
/// Visual Continuity Invariant: "Nothing changes unless explicitly changed."
/// NewState = OldState ⊕ Delta
/// Frame-level actions (Pose, Action, Expression) belong exclusively to TransientVisualState.
/// </summary>
public sealed record SessionSceneState(
    string? CurrentLocation = null,
    string? CurrentPosition = null,
    string? CurrentOutfit = null,
    string? CurrentTimeOfDay = null,
    string? HeldItems = null,
    string? Atmosphere = null,
    int SceneRevision = 0,
    string? LastSceneImageUrl = null,
    DateTime? LastUpdatedAt = null
)
{
    /// <summary>
    /// Applies delta changes onto the persistent scene state.
    /// Any field omitted or null in delta remains strictly unchanged (Invariance).
    /// SceneRevision identifies the committed turn/frame, not the number of persistent-field mutations.
    /// Revision is controlled authoritatively by the application commit boundary.
    /// </summary>
    public SessionSceneState ApplyDelta(
        SceneStateDelta delta,
        int? explicitRevision = null,
        string? newSceneImageUrl = null)
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
            CurrentPosition: !string.IsNullOrWhiteSpace(delta.PositionChange) ? delta.PositionChange.Trim() : this.CurrentPosition,
            CurrentOutfit: !string.IsNullOrWhiteSpace(delta.OutfitChange) ? delta.OutfitChange.Trim() : this.CurrentOutfit,
            CurrentTimeOfDay: !string.IsNullOrWhiteSpace(delta.TimeOfDayChange) ? delta.TimeOfDayChange.Trim() : this.CurrentTimeOfDay,
            HeldItems: resolvedHeldItems,
            Atmosphere: !string.IsNullOrWhiteSpace(delta.AtmosphereChange) ? delta.AtmosphereChange.Trim() : this.Atmosphere,
            SceneRevision: explicitRevision ?? (this.SceneRevision + 1),
            LastSceneImageUrl: newSceneImageUrl ?? this.LastSceneImageUrl,
            LastUpdatedAt: DateTime.UtcNow
        );
    }
}
