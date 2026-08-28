using Application.Contracts.Activities;
using Application.Contracts.Goals;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Contracts.Autonomous;

/// <summary>
/// Execution request conveying the character, decision candidate, and runtime context.
/// Distinct identities:
/// - ExecutionId: Unique invocation identifier / idempotency token.
/// - Candidate.DecisionFingerprint: Deterministic hash of decision factors for reproducibility.
/// </summary>
public sealed record ActivityExecutionRequest(
    Character Character,
    CharacterActivityCandidate Candidate,
    DateTime CurrentTime,
    string TimeBucket,
    Guid? ExecutionId = null,
    CharacterVisualState? CurrentVisualState = null,
    CharacterStateSnapshot? CurrentState = null,
    int SceneRevision = 1
);

/// <summary>
/// Execution result conveying atomic state mutations, goal contributions, and visual moment creation.
/// </summary>
public sealed record ActivityExecutionResult(
    bool Success,
    bool IsDuplicateSuppressed,
    Guid? ExecutionId,
    CharacterActivity? Activity,
    CharacterStateSnapshot? NewState,
    GoalProgressResult? GoalResult,
    bool VisualMomentCreated,
    Guid? SceneIntentId,
    Guid? SceneSpecificationId,
    string Message
);
