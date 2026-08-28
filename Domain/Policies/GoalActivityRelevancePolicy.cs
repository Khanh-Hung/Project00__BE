using Domain.Entities;
using Domain.Enums;

namespace Domain.Policies;

public sealed record GoalRelevanceResult(
    float Score,
    string Reason
);

public static class GoalActivityRelevancePolicy
{
    public static GoalRelevanceResult Evaluate(
        CharacterGoal goal,
        CharacterActivityType activityType)
    {
        ArgumentNullException.ThrowIfNull(goal, nameof(goal));
        return Evaluate(goal.Title, goal.Description, goal.GoalType, activityType);
    }

    public static GoalRelevanceResult Evaluate(
        string title,
        string? description,
        CharacterGoalType goalType,
        CharacterActivityType activityType)
    {
        var text = (title + " " + (description ?? "")).ToLowerInvariant();
        float score = 0.0f;
        string reason = $"Activity {activityType} evaluated for goal '{title}'.";

        // 1. Keyword Overrides for High Direct Relevance
        if ((text.Contains("cook") || text.Contains("baking") || text.Contains("culinary") || text.Contains("recipe")) &&
            activityType == CharacterActivityType.Cooking)
        {
            return new GoalRelevanceResult(0.95f, $"Cooking directly advances culinary goal '{title}'.");
        }

        if ((text.Contains("paint") || text.Contains("drawing") || text.Contains("art") || text.Contains("sculpt") || text.Contains("write") || text.Contains("book")) &&
            (activityType == CharacterActivityType.Working || activityType == CharacterActivityType.Custom))
        {
            return new GoalRelevanceResult(0.95f, $"Creative activity directly advances goal '{title}'.");
        }

        if ((text.Contains("train") || text.Contains("exercise") || text.Contains("fitness") || text.Contains("workout") || text.Contains("strength") || text.Contains("sword")) &&
            activityType == CharacterActivityType.Exercising)
        {
            return new GoalRelevanceResult(0.95f, $"Physical exercise directly advances training goal '{title}'.");
        }

        if ((text.Contains("explore") || text.Contains("ruin") || text.Contains("uncharted") || text.Contains("discover") || text.Contains("survey")) &&
            (activityType == CharacterActivityType.Exploring || activityType == CharacterActivityType.Walking))
        {
            return new GoalRelevanceResult(0.95f, $"Exploration directly advances discovery goal '{title}'.");
        }

        if ((text.Contains("study") || text.Contains("learn") || text.Contains("research") || text.Contains("read") || text.Contains("scholar") || text.Contains("alchemy")) &&
            (activityType == CharacterActivityType.Reading || activityType == CharacterActivityType.Working))
        {
            return new GoalRelevanceResult(0.95f, $"Study and research directly advance intellectual goal '{title}'.");
        }

        // 2. Goal Type Baseline Alignments
        score = goalType switch
        {
            CharacterGoalType.Exploration => activityType switch
            {
                CharacterActivityType.Exploring => 0.90f,
                CharacterActivityType.Walking => 0.70f,
                _ => 0.0f
            },
            CharacterGoalType.SkillDevelopment or CharacterGoalType.Career => activityType switch
            {
                CharacterActivityType.Working => 0.85f,
                CharacterActivityType.Reading => 0.75f,
                CharacterActivityType.Custom => 0.80f,
                _ => 0.0f
            },
            CharacterGoalType.Creative => activityType switch
            {
                CharacterActivityType.Custom => 0.90f,
                CharacterActivityType.Working => 0.80f,
                CharacterActivityType.Relaxing => 0.50f,
                _ => 0.0f
            },
            CharacterGoalType.PersonalGrowth or CharacterGoalType.Lifestyle => activityType switch
            {
                CharacterActivityType.Exercising => 0.80f,
                CharacterActivityType.GettingReady => 0.75f,
                CharacterActivityType.Cooking => 0.70f,
                CharacterActivityType.Reading => 0.65f,
                CharacterActivityType.Bathing => 0.60f,
                _ => 0.0f
            },
            CharacterGoalType.Relationship => activityType switch
            {
                CharacterActivityType.Socializing => 0.90f,
                CharacterActivityType.Eating => 0.65f,
                CharacterActivityType.Drinking => 0.65f,
                CharacterActivityType.Walking => 0.60f,
                _ => 0.0f
            },
            CharacterGoalType.Collection => activityType switch
            {
                CharacterActivityType.Exploring => 0.85f,
                CharacterActivityType.Walking => 0.70f,
                CharacterActivityType.Working => 0.60f,
                _ => 0.0f
            },
            _ => 0.0f
        };

        if (score > 0)
        {
            reason = $"Goal type {goalType} aligns with activity {activityType} for '{title}'.";
        }

        return new GoalRelevanceResult(score, reason);
    }
}
