using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Pure domain outcome of a character reaction, containing state deltas, goal impacts, memory candidates, and activity/visual triggers.
/// </summary>
public sealed record CharacterReaction(
    int MoodDelta = 0,
    CharacterMood? NewMood = null,
    int MoodIntensityDelta = 0,
    int EnergyDelta = 0,
    int StressDelta = 0,
    int HungerDelta = 0,
    int SocialNeedDelta = 0,
    int ConfidenceDelta = 0,
    int RelationshipDelta = 0,
    GoalImpactCandidate? GoalImpact = null,
    MemoryCandidate? MemoryCandidate = null,
    bool ShouldTriggerActivity = false,
    CharacterActivityType? ActivityIntentType = null,
    string? ActivityReason = null,
    ActivityPriority? ActivityPriority = null,
    bool ShouldTriggerVisualMoment = false,
    ActivityPriority? VisualPriority = null,
    string? ActionHint = null,
    string? PoseHint = null,
    string? EnvironmentHint = null,
    string ReactionReason = "",
    ReactionPriority Priority = ReactionPriority.LowValueSystem
);
