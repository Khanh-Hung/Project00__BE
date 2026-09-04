using System;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Contracts.ActionExecution;

public sealed record CharacterActionExecutionContext(
    Guid ExecutionId,
    DateTimeOffset ExecutedAtUtc
);

public enum CharacterActionExecutionStatus
{
    Applied = 1,
    AlreadyExecuted = 2,
    IdempotencyConflict = 3,
    ConcurrencyConflict = 4,
    InvalidProposal = 5,
    NotFound = 6
}

public sealed record CharacterActionExecutionResult(
    Guid ExecutionId,
    Guid CharacterId,
    CharacterActionExecutionStatus Status,
    ActionType? ActionType,
    double? Intensity,
    IntentType? SourceIntent,
    MotivationType? Motivation,
    int StateVersionBefore,
    int StateVersionAfter,
    CharacterStateDelta? AppliedDelta,
    CharacterStateSnapshot? Snapshot = null,
    string? Message = null
)
{
    public bool IsSuccess => Status == CharacterActionExecutionStatus.Applied || Status == CharacterActionExecutionStatus.AlreadyExecuted;
    public bool IsApplied => Status == CharacterActionExecutionStatus.Applied;
    public bool IsDuplicateSuppressed => Status == CharacterActionExecutionStatus.AlreadyExecuted;

    public static CharacterActionExecutionResult Applied(
        Guid executionId,
        Guid characterId,
        CharacterActionProposal proposal,
        int versionBefore,
        int versionAfter,
        CharacterStateDelta delta,
        CharacterStateSnapshot snapshot) =>
        new(
            ExecutionId: executionId,
            CharacterId: characterId,
            Status: CharacterActionExecutionStatus.Applied,
            ActionType: proposal.Type,
            Intensity: proposal.Intensity,
            SourceIntent: proposal.SourceIntent,
            Motivation: proposal.Motivation,
            StateVersionBefore: versionBefore,
            StateVersionAfter: versionAfter,
            AppliedDelta: delta,
            Snapshot: snapshot
        );

    public static CharacterActionExecutionResult AlreadyExecuted(
        Guid executionId,
        Guid characterId,
        CharacterActionProposal proposal,
        int versionBefore,
        int versionAfter,
        CharacterStateDelta delta,
        CharacterStateSnapshot snapshot) =>
        new(
            ExecutionId: executionId,
            CharacterId: characterId,
            Status: CharacterActionExecutionStatus.AlreadyExecuted,
            ActionType: proposal.Type,
            Intensity: proposal.Intensity,
            SourceIntent: proposal.SourceIntent,
            Motivation: proposal.Motivation,
            StateVersionBefore: versionBefore,
            StateVersionAfter: versionAfter,
            AppliedDelta: delta,
            Snapshot: snapshot,
            Message: "Action execution already applied for this execution ID."
        );

    public static CharacterActionExecutionResult IdempotencyConflict(
        Guid executionId,
        Guid characterId,
        CharacterActionProposal proposal,
        string message) =>
        new(
            ExecutionId: executionId,
            CharacterId: characterId,
            Status: CharacterActionExecutionStatus.IdempotencyConflict,
            ActionType: proposal?.Type,
            Intensity: proposal?.Intensity,
            SourceIntent: proposal?.SourceIntent,
            Motivation: proposal?.Motivation,
            StateVersionBefore: 0,
            StateVersionAfter: 0,
            AppliedDelta: null,
            Message: message
        );

    public static CharacterActionExecutionResult ConcurrencyConflict(
        Guid executionId,
        Guid characterId,
        CharacterActionProposal proposal,
        int currentVersion,
        string? message = null) =>
        new(
            ExecutionId: executionId,
            CharacterId: characterId,
            Status: CharacterActionExecutionStatus.ConcurrencyConflict,
            ActionType: proposal?.Type,
            Intensity: proposal?.Intensity,
            SourceIntent: proposal?.SourceIntent,
            Motivation: proposal?.Motivation,
            StateVersionBefore: currentVersion,
            StateVersionAfter: currentVersion,
            AppliedDelta: null,
            Message: message ?? "Optimistic concurrency conflict occurred during action execution."
        );

    public static CharacterActionExecutionResult InvalidProposal(
        Guid executionId,
        Guid characterId,
        CharacterActionProposal? proposal,
        string message) =>
        new(
            ExecutionId: executionId,
            CharacterId: characterId,
            Status: CharacterActionExecutionStatus.InvalidProposal,
            ActionType: proposal?.Type,
            Intensity: proposal?.Intensity,
            SourceIntent: proposal?.SourceIntent,
            Motivation: proposal?.Motivation,
            StateVersionBefore: 0,
            StateVersionAfter: 0,
            AppliedDelta: null,
            Message: message
        );

    public static CharacterActionExecutionResult NotFound(
        Guid executionId,
        Guid characterId,
        CharacterActionProposal proposal,
        string message) =>
        new(
            ExecutionId: executionId,
            CharacterId: characterId,
            Status: CharacterActionExecutionStatus.NotFound,
            ActionType: proposal?.Type,
            Intensity: proposal?.Intensity,
            SourceIntent: proposal?.SourceIntent,
            Motivation: proposal?.Motivation,
            StateVersionBefore: 0,
            StateVersionAfter: 0,
            AppliedDelta: null,
            Message: message
        );
}
