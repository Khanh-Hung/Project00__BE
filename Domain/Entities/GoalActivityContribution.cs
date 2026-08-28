using Domain.Common;

namespace Domain.Entities;

public sealed class GoalActivityContribution : BaseEntity
{
    public Guid GoalId { get; private set; }
    public Guid ActivityId { get; private set; }
    public double ContributionValue { get; private set; }

    private GoalActivityContribution() : base() { }

    public GoalActivityContribution(
        Guid goalId,
        Guid activityId,
        double contributionValue,
        Guid? id = null,
        DateTime? createdAt = null) : base(id ?? Guid.CreateVersion7())
    {
        if (goalId == Guid.Empty)
            throw new ArgumentException("GoalId cannot be empty.", nameof(goalId));

        if (activityId == Guid.Empty)
            throw new ArgumentException("ActivityId cannot be empty.", nameof(activityId));

        if (double.IsNaN(contributionValue) || double.IsInfinity(contributionValue) || contributionValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(contributionValue), "ContributionValue must be a valid number greater than zero.");

        GoalId = goalId;
        ActivityId = activityId;
        ContributionValue = contributionValue;
        if (createdAt.HasValue)
        {
            CreatedAt = createdAt.Value;
        }
    }
}
