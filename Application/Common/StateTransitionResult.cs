using Application.Enums;
using Domain.ValueObjects;

namespace Application.Common;

public sealed record StateTransitionResult(
    StateTransitionResultStatus Status,
    CharacterStateSnapshot? Snapshot,
    int VersionBefore,
    int VersionAfter,
    string? Message = null
)
{
    public bool IsSuccess => Status == StateTransitionResultStatus.Applied || Status == StateTransitionResultStatus.AlreadyApplied;
    public bool IsApplied => Status == StateTransitionResultStatus.Applied;
    public bool IsDuplicateSuppressed => Status == StateTransitionResultStatus.AlreadyApplied;

    public static StateTransitionResult Applied(CharacterStateSnapshot snapshot, int versionBefore, int versionAfter) =>
        new(StateTransitionResultStatus.Applied, snapshot, versionBefore, versionAfter);

    public static StateTransitionResult AlreadyApplied(CharacterStateSnapshot snapshot, int version) =>
        new(StateTransitionResultStatus.AlreadyApplied, snapshot, version, version, "Transition already applied for this execution ID.");

    public static StateTransitionResult IdempotencyConflict(string message) =>
        new(StateTransitionResultStatus.IdempotencyConflict, null, 0, 0, message);

    public static StateTransitionResult ConcurrencyConflict(int currentVersion, string? message = null) =>
        new(StateTransitionResultStatus.ConcurrencyConflict, null, currentVersion, currentVersion, message ?? "Optimistic concurrency conflict occurred.");

    public static StateTransitionResult InvalidEvolutionTime(string message) =>
        new(StateTransitionResultStatus.InvalidEvolutionTime, null, 0, 0, message);

    public static StateTransitionResult NotFound(string message) =>
        new(StateTransitionResultStatus.NotFound, null, 0, 0, message);

    public static StateTransitionResult InvalidState(string message) =>
        new(StateTransitionResultStatus.InvalidState, null, 0, 0, message);
}
