using Application.Contracts.Autonomous;
using Application.Contracts.Reactions;
using Domain.Entities;

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
