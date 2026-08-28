using Domain.Entities;

namespace Application.Contracts.Activities;

/// <summary>
/// Compact contextual request for determining a character's next autonomous activity.
/// </summary>
public sealed record CharacterActivityDecisionRequest(
    Guid CharacterId,
    DateTime CurrentTime,
    string CurrentLocation,
    string TimeBucket,
    CharacterVisualState? CurrentVisualState = null,
    IReadOnlyList<CharacterActivity>? RecentActivities = null,
    IReadOnlyList<CharacterVisualMemory>? RecentVisualMemories = null,
    string? PersonalityPrompt = null,
    string? WorldDescription = null,
    IReadOnlyList<string>? ActiveGoals = null,
    IReadOnlyList<Application.Contracts.Goals.CharacterGoalSnapshot>? Goals = null,
    int SceneRevision = 1
);
