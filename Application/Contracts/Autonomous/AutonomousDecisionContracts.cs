using Application.Contracts.Activities;
using Application.Contracts.Goals;
using Domain.Entities;
using Domain.Policies;
using Domain.ValueObjects;

namespace Application.Contracts.Autonomous;

public enum AutonomousDecisionAction
{
    DoNothing,
    PerformActivity
}

public sealed record AutonomousDecisionRequest(
    Guid CharacterId,
    DateTime CurrentTime,
    string CurrentLocation,
    string TimeBucket,
    CharacterVisualState? CurrentVisualState = null,
    CharacterStateSnapshot? StateSnapshot = null,
    IReadOnlyList<CharacterActivity>? RecentActivities = null,
    IReadOnlyList<CharacterVisualMemory>? RecentVisualMemories = null,
    string? PersonalityPrompt = null,
    string? WorldDescription = null,
    IReadOnlyList<string>? ActiveGoals = null,
    IReadOnlyList<CharacterGoalSnapshot>? Goals = null,
    int SceneRevision = 1
);

public sealed record AutonomousDecisionResult(
    AutonomousDecisionAction Action,
    CharacterActivityCandidate? Candidate,
    ActivityStateDelta? ExpectedStateDelta,
    Guid? TargetGoalId,
    string Reason
);
