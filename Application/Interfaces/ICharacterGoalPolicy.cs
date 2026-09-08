using System;
using System.Collections.Generic;
using Application.Contracts.Goals;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Pure deterministic policy mapping CharacterDesireEvaluation and active goals to a GoalPolicyDecision.
/// Prevents redundant goal creation by strictly reusing compatible active goals across cycles.
/// </summary>
public interface ICharacterGoalPolicy
{
    GoalPolicyDecision Evaluate(
        Guid characterId,
        CharacterDesireEvaluation desireEvaluation,
        IReadOnlyList<CharacterGoal> activeGoals,
        DateTimeOffset now);
}
