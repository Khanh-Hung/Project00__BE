using Domain.Entities;

namespace Application.Interfaces;

public sealed record GoalProgressResult(
    bool Success,
    bool IsDuplicateContribution,
    double ContributionValue,
    float PreviousProgress,
    float NewProgress,
    bool MilestoneCompleted,
    bool GoalCompleted,
    string Message
);

public interface IGoalProgressService
{
    Task<GoalProgressResult> RecordContributionAsync(
        Guid goalId,
        Guid activityId,
        double contributionValue,
        DateTime? now = null,
        CancellationToken ct = default);
}
