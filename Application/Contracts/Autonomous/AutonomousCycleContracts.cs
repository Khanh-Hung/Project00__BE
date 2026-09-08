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
    Failed = 7
}

public sealed record AutonomousCycleResult(
    Guid CharacterId,
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
    string? Message = null)
{
    public bool IsSuccess => Status == AutonomousCycleStatus.Executed;

    public static AutonomousCycleResult FromCognitiveCycleResult(
        Guid characterId,
        Guid cycleId,
        DateTimeOffset simulationTimeUtc,
        CharacterCognitiveCycleResult cycleResult)
    {
        var goalId = cycleResult.GoalContext?.GoalId ?? cycleResult.GoalFeedback?.GoalId;

        AutonomousCycleStatus status;

        if (cycleResult.Status == CharacterCognitiveCycleStatus.CompletedWithAction)
        {
            status = AutonomousCycleStatus.Executed;
        }
        else if (cycleResult.SafetyDecision != null && !cycleResult.SafetyDecision.IsAllowed)
        {
            status = AutonomousCycleStatus.SafetyBlocked;
        }
        else if (cycleResult.Status == CharacterCognitiveCycleStatus.CompletedWithoutAction)
        {
            if (cycleResult.Desires == null ||
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
        }
        else
        {
            status = AutonomousCycleStatus.Failed;
        }

        return new AutonomousCycleResult(
            CharacterId: characterId,
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
            Message: cycleResult.Message
        );
    }

    public static AutonomousCycleResult FailedResult(
        Guid characterId,
        Guid cycleId,
        DateTimeOffset simulationTimeUtc,
        string message) =>
        new(
            CharacterId: characterId,
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
