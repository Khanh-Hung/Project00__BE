using System;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Contracts.Goals;
using Domain.Enums;
using Domain.ValueObjects;

// Aliases to seamlessly support alternative naming conventions
using CharacterIntentResult = Domain.ValueObjects.CharacterIntentEvaluation;
using CharacterActionProposalResult = Domain.ValueObjects.CharacterActionProposalEvaluation;
using CharacterSafetyDecision = Domain.ValueObjects.SafetyDecision;

namespace Application.Contracts.Autonomous;

public enum AutonomousCycleStatus
{
    Executed = 1,
    SafetyBlocked = 2,
    NoDesire = 3,
    NoGoal = 4,
    NoIntent = 5,
    NoActionProposal = 6,
    Failed = 7,
    InvalidInput = 8
}

public sealed record AutonomousCycleResult(
    Guid CharacterId,
    Guid SimulationTickId,
    Guid CycleId,
    DateTimeOffset SimulationTimeUtc,
    AutonomousCycleStatus Status,
    CharacterDesireEvaluation? Desire,
    Guid? GoalId,
    CharacterIntentResult? Intent,
    CharacterActionProposalResult? ActionProposal,
    CharacterSafetyDecision? SafetyDecision,
    CharacterActionExecutionResult? ActionExecutionResult,
    CharacterGoalProgressFeedback? GoalProgressFeedback,
    CharacterSocialPresenceFeedback? SocialPresenceFeedback = null,
    string? Message = null)
{
    public bool IsSuccess => Status == AutonomousCycleStatus.Executed;

    public static AutonomousCycleResult FromCognitiveCycleResult(
        Guid characterId,
        Guid simulationTickId,
        Guid cycleId,
        DateTimeOffset simulationTimeUtc,
        CharacterCognitiveCycleResult cycleResult)
    {
        var goalId = cycleResult.GoalContext?.GoalId ?? cycleResult.GoalFeedback?.GoalId;

        AutonomousCycleStatus status;

        switch (cycleResult.Status)
        {
            case CharacterCognitiveCycleStatus.CompletedWithAction:
            case CharacterCognitiveCycleStatus.AlreadyExecuted:
                status = AutonomousCycleStatus.Executed;
                break;

            case CharacterCognitiveCycleStatus.InvalidInput:
                status = AutonomousCycleStatus.InvalidInput;
                break;

            case CharacterCognitiveCycleStatus.CompletedWithoutAction:
                if (cycleResult.SafetyDecision != null && !cycleResult.SafetyDecision.IsAllowed)
                {
                    status = AutonomousCycleStatus.SafetyBlocked;
                }
                else if (cycleResult.Desires == null ||
                         cycleResult.Desires.DominantDesire == null ||
                         cycleResult.Desires.DominantDesire.Intensity <= 0.0)
                {
                    status = AutonomousCycleStatus.NoDesire;
                }
                else if (cycleResult.GoalContext == null)
                {
                    status = AutonomousCycleStatus.NoGoal;
                }
                else if (cycleResult.Intent?.Intent == null)
                {
                    status = AutonomousCycleStatus.NoIntent;
                }
                else if (cycleResult.ActionProposal?.Proposal == null)
                {
                    status = AutonomousCycleStatus.NoActionProposal;
                }
                else
                {
                    status = AutonomousCycleStatus.Failed;
                }
                break;

            case CharacterCognitiveCycleStatus.NotFound:
            case CharacterCognitiveCycleStatus.ConcurrencyConflict:
            case CharacterCognitiveCycleStatus.IdempotencyConflict:
            case CharacterCognitiveCycleStatus.Failed:
            default:
                // If action execution was already applied, we must NOT report false failure to caller
                if (cycleResult.ActionExecution != null && cycleResult.ActionExecution.IsApplied)
                {
                    status = AutonomousCycleStatus.Executed;
                }
                else
                {
                    status = AutonomousCycleStatus.Failed;
                }
                break;
        }

        return new AutonomousCycleResult(
            CharacterId: characterId,
            SimulationTickId: simulationTickId,
            CycleId: cycleId,
            SimulationTimeUtc: simulationTimeUtc,
            Status: status,
            Desire: cycleResult.Desires,
            GoalId: goalId,
            Intent: cycleResult.Intent,
            ActionProposal: cycleResult.ActionProposal,
            SafetyDecision: cycleResult.SafetyDecision,
            ActionExecutionResult: cycleResult.ActionExecution,
            GoalProgressFeedback: cycleResult.GoalFeedback,
            SocialPresenceFeedback: cycleResult.SocialPresenceFeedback,
            Message: cycleResult.Message
        );
    }

    public static AutonomousCycleResult DuplicateOrAlreadyExecuted(
        Guid characterId,
        Guid simulationTickId,
        Guid cycleId,
        DateTimeOffset simulationTimeUtc,
        AutonomousCycleStatus status = AutonomousCycleStatus.Executed,
        string? message = null) =>
        new(
            CharacterId: characterId,
            SimulationTickId: simulationTickId,
            CycleId: cycleId,
            SimulationTimeUtc: simulationTimeUtc,
            Status: status,
            Desire: null,
            GoalId: null,
            Intent: null,
            ActionProposal: null,
            SafetyDecision: null,
            ActionExecutionResult: null,
            GoalProgressFeedback: null,
            Message: message ?? "Autonomous tick already executed."
        );

    public static AutonomousCycleResult FailedResult(
        Guid characterId,
        Guid simulationTickId,
        Guid cycleId,
        DateTimeOffset simulationTimeUtc,
        string message) =>
        new(
            CharacterId: characterId,
            SimulationTickId: simulationTickId,
            CycleId: cycleId,
            SimulationTimeUtc: simulationTimeUtc,
            Status: AutonomousCycleStatus.Failed,
            Desire: null,
            GoalId: null,
            Intent: null,
            ActionProposal: null,
            SafetyDecision: null,
            ActionExecutionResult: null,
            GoalProgressFeedback: null,
            Message: message
        );
}
