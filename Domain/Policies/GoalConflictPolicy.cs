using Domain.Entities;
using Domain.Enums;

namespace Domain.Policies;

public static class GoalConflictPolicy
{
    public static CharacterGoal? ResolveGoalConflict(
        IEnumerable<CharacterGoal> activeGoals,
        CharacterActivityType activityType)
    {
        if (activeGoals == null) return null;

        var evaluated = activeGoals
            .Where(g => g.Status == CharacterGoalStatus.Active)
            .Select(g => new
            {
                Goal = g,
                PriorityValue = (int)g.Priority,
                Relevance = GoalActivityRelevancePolicy.Evaluate(g, activityType)
            })
            .Where(x => x.Relevance.Score > 0.1f)
            .OrderByDescending(x => x.PriorityValue)
            .ThenByDescending(x => x.Relevance.Score)
            .ThenBy(x => x.Goal.Id) // Deterministic tie-breaker
            .FirstOrDefault();

        return evaluated?.Goal;
    }
}
