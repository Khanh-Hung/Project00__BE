using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

public sealed class CharacterGoalMilestone : BaseEntity
{
    public Guid GoalId { get; private set; }
    public string Title { get; private set; }
    public string? Description { get; private set; }
    public int Order { get; private set; }
    public double TargetValue { get; private set; }
    public double CurrentValue { get; private set; }
    public CharacterGoalMilestoneStatus Status { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    private CharacterGoalMilestone() : base() 
    {
        Title = null!;
    }

    public CharacterGoalMilestone(
        Guid goalId,
        string title,
        int order,
        double targetValue,
        string? description = null,
        Guid? id = null) : base(id ?? Guid.CreateVersion7())
    {
        if (goalId == Guid.Empty)
            throw new ArgumentException("GoalId cannot be empty.", nameof(goalId));

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty.", nameof(title));

        if (targetValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetValue), "TargetValue must be greater than zero.");

        if (order < 0)
            throw new ArgumentOutOfRangeException(nameof(order), "Order cannot be negative.");

        GoalId = goalId;
        Title = title.Trim();
        Description = description?.Trim();
        Order = order;
        TargetValue = targetValue;
        CurrentValue = 0;
        Status = CharacterGoalMilestoneStatus.Pending;
    }

    public void Activate()
    {
        if (Status == CharacterGoalMilestoneStatus.Completed)
            throw new InvalidOperationException("Completed milestone cannot become Active.");

        Status = CharacterGoalMilestoneStatus.Active;
        Touch();
    }

    public void RecordProgress(double amount, DateTime? now = null)
    {
        if (Status == CharacterGoalMilestoneStatus.Completed)
            return;

        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Progress amount cannot be negative.");

        CurrentValue += amount;
        if (CurrentValue >= TargetValue)
        {
            CurrentValue = TargetValue;
            Complete(now ?? DateTime.UtcNow);
        }
        else
        {
            Touch();
        }
    }

    public void Complete(DateTime? now = null)
    {
        if (Status == CharacterGoalMilestoneStatus.Completed)
            return;

        Status = CharacterGoalMilestoneStatus.Completed;
        CurrentValue = TargetValue;
        CompletedAt = now ?? DateTime.UtcNow;
        Touch();
    }

    public void Skip()
    {
        if (Status == CharacterGoalMilestoneStatus.Completed)
            throw new InvalidOperationException("Completed milestone cannot be skipped.");

        Status = CharacterGoalMilestoneStatus.Skipped;
        Touch();
    }
}
