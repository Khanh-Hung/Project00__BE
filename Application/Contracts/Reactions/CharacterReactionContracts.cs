using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Contracts.Reactions;

/// <summary>
/// Execution request for processing a CharacterWorldEvent.
/// Conveys the caller-owned ExecutionId and runtime contextual snapshots.
/// </summary>
public sealed record ReactionExecutionRequest(
    CharacterWorldEvent WorldEvent,
    Character Character,
    Guid ExecutionId,
    DateTime CurrentTime,
    CharacterStateSnapshot? CurrentState = null,
    CharacterVisualState? CurrentVisualState = null,
    IReadOnlyList<GoalSnapshot>? CurrentGoals = null,
    CharacterActivity? CurrentActivity = null,
    int SceneRevision = 1
);

/// <summary>
/// Execution result conveying atomic state mutations, goal contributions, memories, and visual moments.
/// </summary>
public sealed record ReactionExecutionResult(
    bool Success,
    bool IsDuplicateSuppressed,
    Guid ExecutionId,
    CharacterWorldEventReaction? Reaction,
    CharacterStateSnapshot? NewState,
    bool MemoryCreated,
    Guid? MemoryId,
    bool GoalContributed,
    Guid? GoalId,
    double? GoalContributionValue,
    bool VisualMomentCreated,
    Guid? SceneIntentId,
    Guid? SceneSpecificationId,
    bool ActivityTriggered,
    string Message
);
