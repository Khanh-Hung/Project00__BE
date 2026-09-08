using System;
using System.Collections.Generic;
using System.Linq;
using Application.Contracts.Goals;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Services.Goals;

/// <summary>
/// Deterministic domain policy mapping character desires to character goals.
/// Enforces semantic reuse: returns compatible existing active goals rather than generating duplicates.
/// Zero side-effects, zero LLM, zero DB calls, zero clock calls.
/// </summary>
public sealed class CharacterGoalPolicy : ICharacterGoalPolicy
{
    public GoalPolicyDecision Evaluate(
        Guid characterId,
        CharacterDesireEvaluation desireEvaluation,
        IReadOnlyList<CharacterGoal> activeGoals,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(desireEvaluation, nameof(desireEvaluation));
        activeGoals ??= Array.Empty<CharacterGoal>();

        var dominant = desireEvaluation.DominantDesire;
        if (dominant == null || dominant.Intensity <= 0.0)
        {
            return GoalPolicyDecision.None;
        }

        var candidate = MapDesireToCandidate(dominant);
        if (candidate == null)
        {
            return GoalPolicyDecision.None;
        }

        // Semantic reuse check: if an active goal with the same GoalKey exists for this character, reuse it
        var existingCompatibleGoal = activeGoals
            .FirstOrDefault(g => g.Status == CharacterGoalStatus.Active &&
                                 string.Equals(g.Title, candidate.GoalKey, StringComparison.OrdinalIgnoreCase));

        if (existingCompatibleGoal != null)
        {
            return GoalPolicyDecision.Reuse(existingCompatibleGoal);
        }

        return GoalPolicyDecision.Create(candidate);
    }

    private static GoalCreationCandidate? MapDesireToCandidate(CharacterDesire desire) =>
        desire.Type switch
        {
            DesireType.NeedSocialConnection => new GoalCreationCandidate(
                GoalKey: "BuildRelationship",
                GoalType: CharacterGoalType.Relationship,
                Priority: 80,
                Description: "Develop and strengthen meaningful social connection."
            ),
            DesireType.NeedRest => new GoalCreationCandidate(
                GoalKey: "Rest",
                GoalType: CharacterGoalType.Lifestyle,
                Priority: 70,
                Description: "Recover energy and rest."
            ),
            DesireType.NeedFood => new GoalCreationCandidate(
                GoalKey: "Eat",
                GoalType: CharacterGoalType.Lifestyle,
                Priority: 60,
                Description: "Seek nourishment and food."
            ),
            DesireType.NeedReduceStress => new GoalCreationCandidate(
                GoalKey: "ReduceStress",
                GoalType: CharacterGoalType.Lifestyle,
                Priority: 50,
                Description: "Perform relaxing activities to reduce stress."
            ),
            _ => null // Unsupported desire types do not spawn autonomous goals in MVP
        };
}
