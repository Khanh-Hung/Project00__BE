using System;
using Application.Contracts.ActionExecution;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Contracts.CognitiveCycle;

public enum CognitiveEventType
{
    UserMessage = 1,
    WorldEvent = 2
}

public abstract record CharacterCognitiveEvent(
    Guid EventId,
    Guid CharacterId,
    CognitiveEventType EventType,
    DateTimeOffset OccurredAtUtc,
    string Source
);

public sealed record UserMessageCognitiveEvent(
    Guid EventId,
    Guid CharacterId,
    DateTimeOffset OccurredAtUtc,
    string Source,
    string Message
) : CharacterCognitiveEvent(EventId, CharacterId, CognitiveEventType.UserMessage, OccurredAtUtc, Source);

public sealed record WorldCognitiveEvent(
    Guid EventId,
    Guid CharacterId,
    DateTimeOffset OccurredAtUtc,
    string Source,
    string EventName,
    string? Category = null
) : CharacterCognitiveEvent(EventId, CharacterId, CognitiveEventType.WorldEvent, OccurredAtUtc, Source);

public sealed record CharacterCognitiveCycleContext(
    Guid CycleId,
    Guid ExecutionId,
    Guid CharacterId,
    DateTimeOffset TriggeredAtUtc,
    CharacterCognitiveEvent? Event = null,
    CharacterPerceptionContext? PerceptionContext = null,
    CharacterBlueprint? Blueprint = null
);

public enum CharacterCognitiveCycleStatus
{
    CompletedWithAction = 1,
    CompletedWithoutAction = 2,
    AlreadyExecuted = 3,
    ConcurrencyConflict = 4,
    IdempotencyConflict = 5,
    InvalidInput = 6,
    NotFound = 7,
    Failed = 8
}

public sealed record CharacterCognitiveCycleResult(
    Guid CycleId,
    Guid ExecutionId,
    Guid CharacterId,
    DateTimeOffset TriggeredAtUtc,
    CharacterCognitiveCycleStatus Status,
    int StateVersionAtStart,
    CharacterCognitiveEvent? Event = null,
    CharacterInternalExperience? Experience = null,
    CharacterAppraisal? Appraisal = null,
    CharacterEmotion? Emotion = null,
    CharacterDesireEvaluation? Desires = null,
    CharacterIntentEvaluation? Intent = null,
    CharacterActionProposalEvaluation? ActionProposal = null,
    CharacterActionExecutionResult? ActionExecution = null,
    string? Message = null
)
{
    public bool IsSuccess => Status == CharacterCognitiveCycleStatus.CompletedWithAction
                          || Status == CharacterCognitiveCycleStatus.CompletedWithoutAction
                          || Status == CharacterCognitiveCycleStatus.AlreadyExecuted;

    public bool HasAction => Status == CharacterCognitiveCycleStatus.CompletedWithAction;
    public bool IsDuplicateSuppressed => Status == CharacterCognitiveCycleStatus.AlreadyExecuted;

    public static CharacterCognitiveCycleResult CompletedWithAction(
        Guid cycleId,
        Guid executionId,
        Guid characterId,
        DateTimeOffset triggeredAtUtc,
        int stateVersionAtStart,
        CharacterInternalExperience experience,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion,
        CharacterDesireEvaluation desires,
        CharacterIntentEvaluation intent,
        CharacterActionProposalEvaluation actionProposal,
        CharacterActionExecutionResult actionExecution,
        CharacterCognitiveEvent? @event = null) =>
        new(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc,
            Status: CharacterCognitiveCycleStatus.CompletedWithAction,
            StateVersionAtStart: stateVersionAtStart,
            Event: @event,
            Experience: experience,
            Appraisal: appraisal,
            Emotion: emotion,
            Desires: desires,
            Intent: intent,
            ActionProposal: actionProposal,
            ActionExecution: actionExecution
        );

    public static CharacterCognitiveCycleResult CompletedWithoutAction(
        Guid cycleId,
        Guid executionId,
        Guid characterId,
        DateTimeOffset triggeredAtUtc,
        int stateVersionAtStart,
        CharacterInternalExperience? experience = null,
        CharacterAppraisal? appraisal = null,
        CharacterEmotion? emotion = null,
        CharacterDesireEvaluation? desires = null,
        CharacterIntentEvaluation? intent = null,
        CharacterActionProposalEvaluation? actionProposal = null,
        CharacterCognitiveEvent? @event = null,
        string? message = null) =>
        new(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc,
            Status: CharacterCognitiveCycleStatus.CompletedWithoutAction,
            StateVersionAtStart: stateVersionAtStart,
            Event: @event,
            Experience: experience,
            Appraisal: appraisal,
            Emotion: emotion,
            Desires: desires,
            Intent: intent,
            ActionProposal: actionProposal,
            ActionExecution: null,
            Message: message ?? "Cognitive cycle completed without actionable proposal."
        );

    public static CharacterCognitiveCycleResult AlreadyExecuted(
        Guid cycleId,
        Guid executionId,
        Guid characterId,
        DateTimeOffset triggeredAtUtc,
        int stateVersionAtStart,
        CharacterInternalExperience experience,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion,
        CharacterDesireEvaluation desires,
        CharacterIntentEvaluation intent,
        CharacterActionProposalEvaluation actionProposal,
        CharacterActionExecutionResult actionExecution,
        CharacterCognitiveEvent? @event = null) =>
        new(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc,
            Status: CharacterCognitiveCycleStatus.AlreadyExecuted,
            StateVersionAtStart: stateVersionAtStart,
            Event: @event,
            Experience: experience,
            Appraisal: appraisal,
            Emotion: emotion,
            Desires: desires,
            Intent: intent,
            ActionProposal: actionProposal,
            ActionExecution: actionExecution,
            Message: "Action execution was previously applied for this ExecutionId."
        );

    public static CharacterCognitiveCycleResult ConcurrencyConflict(
        Guid cycleId,
        Guid executionId,
        Guid characterId,
        DateTimeOffset triggeredAtUtc,
        int stateVersionAtStart,
        CharacterInternalExperience? experience = null,
        CharacterAppraisal? appraisal = null,
        CharacterEmotion? emotion = null,
        CharacterDesireEvaluation? desires = null,
        CharacterIntentEvaluation? intent = null,
        CharacterActionProposalEvaluation? actionProposal = null,
        CharacterActionExecutionResult? actionExecution = null,
        CharacterCognitiveEvent? @event = null,
        string? message = null) =>
        new(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc,
            Status: CharacterCognitiveCycleStatus.ConcurrencyConflict,
            StateVersionAtStart: stateVersionAtStart,
            Event: @event,
            Experience: experience,
            Appraisal: appraisal,
            Emotion: emotion,
            Desires: desires,
            Intent: intent,
            ActionProposal: actionProposal,
            ActionExecution: actionExecution,
            Message: message ?? "State concurrency conflict occurred during cognitive cycle."
        );

    public static CharacterCognitiveCycleResult IdempotencyConflict(
        Guid cycleId,
        Guid executionId,
        Guid characterId,
        DateTimeOffset triggeredAtUtc,
        int stateVersionAtStart,
        CharacterInternalExperience? experience = null,
        CharacterAppraisal? appraisal = null,
        CharacterEmotion? emotion = null,
        CharacterDesireEvaluation? desires = null,
        CharacterIntentEvaluation? intent = null,
        CharacterActionProposalEvaluation? actionProposal = null,
        CharacterActionExecutionResult? actionExecution = null,
        CharacterCognitiveEvent? @event = null,
        string? message = null) =>
        new(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc,
            Status: CharacterCognitiveCycleStatus.IdempotencyConflict,
            StateVersionAtStart: stateVersionAtStart,
            Event: @event,
            Experience: experience,
            Appraisal: appraisal,
            Emotion: emotion,
            Desires: desires,
            Intent: intent,
            ActionProposal: actionProposal,
            ActionExecution: actionExecution,
            Message: message ?? "Idempotency conflict occurred during cognitive cycle."
        );

    public static CharacterCognitiveCycleResult InvalidInput(
        Guid cycleId,
        Guid executionId,
        Guid characterId,
        DateTimeOffset triggeredAtUtc,
        string message,
        CharacterCognitiveEvent? @event = null) =>
        new(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc,
            Status: CharacterCognitiveCycleStatus.InvalidInput,
            StateVersionAtStart: 0,
            Event: @event,
            Message: message
        );

    public static CharacterCognitiveCycleResult NotFound(
        Guid cycleId,
        Guid executionId,
        Guid characterId,
        DateTimeOffset triggeredAtUtc,
        string message,
        CharacterCognitiveEvent? @event = null) =>
        new(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc,
            Status: CharacterCognitiveCycleStatus.NotFound,
            StateVersionAtStart: 0,
            Event: @event,
            Message: message
        );

    public static CharacterCognitiveCycleResult Failed(
        Guid cycleId,
        Guid executionId,
        Guid characterId,
        DateTimeOffset triggeredAtUtc,
        int stateVersionAtStart,
        CharacterActionExecutionResult? actionExecution = null,
        CharacterCognitiveEvent? @event = null,
        string? message = null) =>
        new(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc,
            Status: CharacterCognitiveCycleStatus.Failed,
            StateVersionAtStart: stateVersionAtStart,
            Event: @event,
            ActionExecution: actionExecution,
            Message: message ?? "Cognitive cycle failed during action execution."
        );
}
