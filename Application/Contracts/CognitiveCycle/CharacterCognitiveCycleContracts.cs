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
    DateTimeOffset OccurredAtUtc,
    string Source
)
{
    public CognitiveEventType EventType => this switch
    {
        UserMessageCognitiveEvent => CognitiveEventType.UserMessage,
        WorldCognitiveEvent => CognitiveEventType.WorldEvent,
        _ => throw new InvalidOperationException($"Unsupported cognitive event type: {GetType().Name}")
    };
}

public sealed record UserMessageCognitiveEvent(
    Guid EventId,
    Guid CharacterId,
    DateTimeOffset OccurredAtUtc,
    string Message,
    string Source = "User"
) : CharacterCognitiveEvent(EventId, CharacterId, OccurredAtUtc, Source);

public sealed record WorldCognitiveEvent(
    Guid EventId,
    Guid CharacterId,
    DateTimeOffset OccurredAtUtc,
    string EventName,
    string Source = "World",
    string? Category = null
) : CharacterCognitiveEvent(EventId, CharacterId, OccurredAtUtc, Source);

public sealed record CharacterCognitiveCycleContext(
    Guid CycleId,
    Guid ExecutionId,
    Guid CharacterId,
    DateTimeOffset TriggeredAtUtc,
    CharacterCognitiveEvent? Event = null,
    CharacterPerceptionContext? PerceptionContext = null,
    CharacterBlueprint? Blueprint = null,
    CharacterMemoryContext? MemoryContext = null
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
    CharacterMemoryContext? MemoryContext = null,
    CharacterMemoryFeedback? MemoryFeedback = null,
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
        CharacterCognitiveEvent? @event = null,
        CharacterMemoryContext? memoryContext = null,
        CharacterMemoryFeedback? memoryFeedback = null) =>
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
            ActionExecution: actionExecution,
            MemoryContext: memoryContext,
            MemoryFeedback: memoryFeedback
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
        CharacterMemoryContext? memoryContext = null,
        CharacterMemoryFeedback? memoryFeedback = null,
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
            MemoryContext: memoryContext,
            MemoryFeedback: memoryFeedback,
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
        CharacterCognitiveEvent? @event = null,
        CharacterMemoryContext? memoryContext = null,
        CharacterMemoryFeedback? memoryFeedback = null) =>
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
            MemoryContext: memoryContext,
            MemoryFeedback: memoryFeedback,
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
        CharacterMemoryContext? memoryContext = null,
        CharacterMemoryFeedback? memoryFeedback = null,
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
            MemoryContext: memoryContext,
            MemoryFeedback: memoryFeedback,
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
        CharacterMemoryContext? memoryContext = null,
        CharacterMemoryFeedback? memoryFeedback = null,
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
            MemoryContext: memoryContext,
            MemoryFeedback: memoryFeedback,
            Message: message ?? "Idempotency conflict occurred during cognitive cycle."
        );

    public static CharacterCognitiveCycleResult InvalidInput(
        Guid cycleId,
        Guid executionId,
        Guid characterId,
        DateTimeOffset triggeredAtUtc,
        string message,
        CharacterCognitiveEvent? @event = null,
        CharacterMemoryContext? memoryContext = null) =>
        new(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc,
            Status: CharacterCognitiveCycleStatus.InvalidInput,
            StateVersionAtStart: 0,
            Event: @event,
            MemoryContext: memoryContext,
            Message: message
        );

    public static CharacterCognitiveCycleResult NotFound(
        Guid cycleId,
        Guid executionId,
        Guid characterId,
        DateTimeOffset triggeredAtUtc,
        string message,
        CharacterCognitiveEvent? @event = null,
        CharacterMemoryContext? memoryContext = null) =>
        new(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: triggeredAtUtc,
            Status: CharacterCognitiveCycleStatus.NotFound,
            StateVersionAtStart: 0,
            Event: @event,
            MemoryContext: memoryContext,
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
        CharacterMemoryContext? memoryContext = null,
        CharacterMemoryFeedback? memoryFeedback = null,
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
            MemoryContext: memoryContext,
            MemoryFeedback: memoryFeedback,
            Message: message ?? "Cognitive cycle failed during action execution."
        );
}
