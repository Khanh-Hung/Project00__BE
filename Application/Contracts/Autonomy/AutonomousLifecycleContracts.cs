using Application.Contracts.Autonomous;
using Application.Contracts.Goals;
using Application.Contracts.Reactions;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Contracts.Autonomy;

/// <summary>
/// Execution request to trigger one discrete autonomous character lifecycle tick.
/// </summary>
public sealed record AutonomyTickRequest(
    Guid CharacterId,
    Guid ExecutionId,
    string TimeBucket,
    DateTime CurrentTime,
    Guid? WorldEventId = null,
    string? CorrelationId = null
);

/// <summary>
/// Loaded domain context required for perception, reaction, autonomous decision, and activity execution.
/// </summary>
public sealed record AutonomousCharacterContext(
    Character Character,
    CharacterVisualState? CurrentVisualState,
    string CurrentLocation,
    int SceneRevision,
    CharacterStateSnapshot CurrentState,
    IReadOnlyList<CharacterActivity> RecentActivities,
    IReadOnlyList<CharacterVisualMemory> RecentVisualMemories,
    IReadOnlyList<CharacterGoalSnapshot> GoalSnapshots,
    IReadOnlyList<GoalSnapshot> GoalSnapshotsForReaction,
    IReadOnlyList<string>? ActiveGoals
);

/// <summary>
/// Result conveying the atomic outcome of an autonomous character lifecycle tick.
/// </summary>
public sealed record AutonomyTickResult(
    bool Success,
    bool IsDuplicateSuppressed,
    Guid ExecutionId,
    CharacterAutonomyTick? Tick,
    ReactionExecutionResult? ReactionResult,
    ActivityExecutionResult? ActivityResult,
    string Message
);
