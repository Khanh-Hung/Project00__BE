using Domain.Entities;
using Domain.Enums;

namespace Domain.Policies;

public sealed record VisualMomentDecision(
    bool ShouldGenerate,
    ActivityPriority Priority,
    string Reason,
    TimeSpan? CooldownRemaining = null
);

/// <summary>
/// Authoritative policy evaluating whether a CharacterActivity qualifies as a visually meaningful moment
/// worthy of triggering an autonomous image synthesis request.
/// Enforces anti-spam cooldowns and activity significance filtering.
/// </summary>
public static class VisualMomentPolicy
{
    public static readonly TimeSpan DefaultVisualMomentCooldown = TimeSpan.FromHours(1);

    public static VisualMomentDecision Evaluate(
        CharacterActivityType activityType,
        string activityLocation,
        CharacterVisualState? currentVisualState,
        DateTime currentTime,
        DateTime? lastVisualGenerationAt,
        TimeSpan? customCooldown = null)
    {
        var cooldown = customCooldown ?? DefaultVisualMomentCooldown;

        // 1. Visual Spam Protection Cooldown Check
        if (lastVisualGenerationAt.HasValue)
        {
            var elapsed = currentTime - lastVisualGenerationAt.Value;
            if (elapsed < cooldown)
            {
                var remaining = cooldown - elapsed;
                return new VisualMomentDecision(
                    ShouldGenerate: false,
                    Priority: ActivityPriority.Low,
                    Reason: $"Visual spam cooldown active ({remaining.TotalMinutes:F0} min remaining).",
                    CooldownRemaining: remaining
                );
            }
        }

        // 2. New Location Detection
        bool isNewLocation = currentVisualState != null && 
            !string.IsNullOrWhiteSpace(currentVisualState.Location) &&
            !string.Equals(currentVisualState.Location.Trim(), activityLocation.Trim(), StringComparison.OrdinalIgnoreCase);

        if (isNewLocation)
        {
            return new VisualMomentDecision(
                ShouldGenerate: true,
                Priority: ActivityPriority.High,
                Reason: $"Transition to new location '{activityLocation}' is visually significant."
            );
        }

        // 3. Activity Significance Evaluation
        return activityType switch
        {
            CharacterActivityType.GettingReady => new VisualMomentDecision(
                ShouldGenerate: true,
                Priority: ActivityPriority.High,
                Reason: "Getting ready involves high-value visual appearance and outfit presentation."
            ),
            CharacterActivityType.Exploring => new VisualMomentDecision(
                ShouldGenerate: true,
                Priority: ActivityPriority.High,
                Reason: "Exploring new environments provides strong scenic visual interest."
            ),
            CharacterActivityType.Bathing => new VisualMomentDecision(
                ShouldGenerate: true,
                Priority: ActivityPriority.High,
                Reason: "Bathing scene involves distinct mood, atmosphere, and visual styling."
            ),
            CharacterActivityType.Sleeping => new VisualMomentDecision(
                ShouldGenerate: true,
                Priority: ActivityPriority.Normal,
                Reason: "Resting/sleeping scene establishes clear temporal and atmospheric change."
            ),
            CharacterActivityType.Cooking => new VisualMomentDecision(
                ShouldGenerate: true,
                Priority: ActivityPriority.Normal,
                Reason: "Cooking scene includes active object interaction and environmental props."
            ),
            CharacterActivityType.Exercising => new VisualMomentDecision(
                ShouldGenerate: true,
                Priority: ActivityPriority.Normal,
                Reason: "Physical exercise scene features dynamic posing and action."
            ),
            CharacterActivityType.Walking => new VisualMomentDecision(
                ShouldGenerate: false,
                Priority: ActivityPriority.Low,
                Reason: "Routine walking in same location is not sufficiently distinctive."
            ),
            CharacterActivityType.Reading => new VisualMomentDecision(
                ShouldGenerate: false,
                Priority: ActivityPriority.Low,
                Reason: "Routine reading is a quiet sedentary activity, filtered by visual moment policy."
            ),
            CharacterActivityType.Eating or CharacterActivityType.Drinking => new VisualMomentDecision(
                ShouldGenerate: false,
                Priority: ActivityPriority.Low,
                Reason: "Routine consumption is standard character maintenance, filtered by visual moment policy."
            ),
            CharacterActivityType.Idle or CharacterActivityType.Relaxing or CharacterActivityType.Working => new VisualMomentDecision(
                ShouldGenerate: false,
                Priority: ActivityPriority.Low,
                Reason: "Routine sedentary state does not warrant autonomous visual generation."
            ),
            CharacterActivityType.Custom => new VisualMomentDecision(
                ShouldGenerate: true,
                Priority: ActivityPriority.Normal,
                Reason: "Custom activity accepted as distinct visual event."
            ),
            _ => new VisualMomentDecision(
                ShouldGenerate: false,
                Priority: ActivityPriority.Low,
                Reason: "Unclassified activity filtered by visual moment policy."
            )
        };
    }
}
