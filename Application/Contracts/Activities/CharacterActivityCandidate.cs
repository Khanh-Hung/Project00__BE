using Domain.Enums;

namespace Application.Contracts.Activities;

/// <summary>
/// Evaluated activity candidate produced deterministically by the activity decision engine.
/// </summary>
public sealed record CharacterActivityCandidate(
    CharacterActivityType ActivityType,
    string Location,
    string Reason,
    ActivityPriority Priority,
    int DurationMinutes,
    bool ShouldCreateVisualMoment,
    float Confidence,
    string ActionHint,
    string? PoseHint = null,
    string? OutfitHint = null,
    string? EnvironmentHint = null,
    string DecisionFingerprint = ""
);
