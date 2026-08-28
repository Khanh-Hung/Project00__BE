using Application.Contracts.Activities;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Contracts.Autonomous;

public sealed record ActivityExecutionRequest(
    Character Character,
    CharacterActivityCandidate Candidate,
    DateTime CurrentTime,
    string TimeBucket,
    CharacterVisualState? CurrentVisualState = null,
    CharacterStateSnapshot? CurrentState = null,
    int SceneRevision = 1
);

public sealed record ActivityExecutionResult(
    bool Success,
    bool IsDuplicateSuppressed,
    CharacterActivity? Activity,
    CharacterStateSnapshot? NewState,
    GoalProgressResult? GoalResult,
    bool VisualMomentCreated,
    Guid? SceneIntentId,
    Guid? SceneSpecificationId,
    string Message
);
