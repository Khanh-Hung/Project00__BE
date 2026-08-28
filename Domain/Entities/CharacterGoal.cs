using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

public sealed class CharacterGoal : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public string Title { get; private set; }
    public string? Description { get; private set; }
    public CharacterGoalType GoalType { get; private set; }
    public CharacterGoalStatus Status { get; private set; }
    public CharacterGoalPriority Priority { get; private set; }
    public float Progress { get; private set; }
    public double TargetValue { get; private set; }
    public double CurrentValue { get; private set; }
    public uint Version { get; private set; } = 1;

    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public DateTime? PausedAt { get; private set; }

    private readonly List<CharacterGoalMilestone> _milestones = new();
    public IReadOnlyList<CharacterGoalMilestone> Milestones => _milestones.AsReadOnly();

    private CharacterGoal() : base() 
    {
        Title = null!;
    }

    public CharacterGoal(
        Guid characterId,
        string title,
        CharacterGoalType goalType,
        double targetValue,
        CharacterGoalPriority priority = CharacterGoalPriority.Normal,
        string? description = null,
        CharacterGoalStatus initialStatus = CharacterGoalStatus.Active,
        Guid? id = null,
        DateTime? now = null) : base(id ?? Guid.CreateVersion7())
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty.", nameof(title));

        if (targetValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetValue), "TargetValue must be greater than zero.");

        var time = now ?? DateTime.UtcNow;

        CharacterId = characterId;
        Title = title.Trim();
        Description = description?.Trim();
        GoalType = goalType;
        Priority = priority;
        TargetValue = targetValue;
        CurrentValue = 0;
        Progress = 0f;
        Status = initialStatus;

        if (Status == CharacterGoalStatus.Active)
        {
            StartedAt = time;
        }
    }

    public void Activate(DateTime? now = null)
    {
        if (Status == CharacterGoalStatus.Completed)
            throw new InvalidOperationException("Completed goal cannot become Active.");

        if (Status == CharacterGoalStatus.Cancelled)
            throw new InvalidOperationException("Cancelled goal cannot become Active.");

        Status = CharacterGoalStatus.Active;
        StartedAt ??= now ?? DateTime.UtcNow;
        PausedAt = null;
        Version++;
        Touch();
    }

    public void Pause(DateTime? now = null)
    {
        if (Status != CharacterGoalStatus.Active)
            throw new InvalidOperationException($"Cannot pause a goal with status {Status}. Must be Active.");

        Status = CharacterGoalStatus.Paused;
        PausedAt = now ?? DateTime.UtcNow;
        Version++;
        Touch();
    }

    public void Resume(DateTime? now = null)
    {
        if (Status != CharacterGoalStatus.Paused)
            throw new InvalidOperationException($"Cannot resume a goal with status {Status}. Must be Paused.");

        Status = CharacterGoalStatus.Active;
        PausedAt = null;
        Version++;
        Touch();
    }

    public void Complete(DateTime? now = null)
    {
        if (Status == CharacterGoalStatus.Completed)
            return;

        if (Status == CharacterGoalStatus.Paused)
            throw new InvalidOperationException("Paused goal cannot complete without Resume.");

        if (Status == CharacterGoalStatus.Cancelled)
            throw new InvalidOperationException("Cancelled goal cannot be completed.");

        Status = CharacterGoalStatus.Completed;
        CompletedAt = now ?? DateTime.UtcNow;
        CurrentValue = Math.Max(CurrentValue, TargetValue);
        Progress = 1.0f;

        foreach (var milestone in _milestones.Where(m => m.Status == CharacterGoalMilestoneStatus.Active))
        {
            milestone.Complete(CompletedAt);
        }

        Version++;
        Touch();
    }

    public void Cancel(DateTime? now = null)
    {
        if (Status == CharacterGoalStatus.Completed)
            throw new InvalidOperationException("Completed goal cannot be cancelled.");

        Status = CharacterGoalStatus.Cancelled;
        CancelledAt = now ?? DateTime.UtcNow;
        Version++;
        Touch();
    }

    public CharacterGoalMilestone AddMilestone(string title, int order, double targetValue, string? description = null)
    {
        if (_milestones.Any(m => m.Order == order))
            throw new ArgumentException($"Milestone with order {order} already exists.", nameof(order));

        var milestone = new CharacterGoalMilestone(Id, title, order, targetValue, description);
        _milestones.Add(milestone);
        _milestones.Sort((a, b) => a.Order.CompareTo(b.Order));

        if (!_milestones.Any(m => m.Status == CharacterGoalMilestoneStatus.Active || m.Status == CharacterGoalMilestoneStatus.Completed))
        {
            milestone.Activate();
        }

        Version++;
        Touch();
        return milestone;
    }

    public void RecordProgress(double incrementValue, DateTime? now = null)
    {
        if (Status != CharacterGoalStatus.Active)
            throw new InvalidOperationException($"Cannot record progress on a goal with status {Status}.");

        if (incrementValue < 0)
            throw new ArgumentOutOfRangeException(nameof(incrementValue), "Progress increment cannot be negative.");

        var time = now ?? DateTime.UtcNow;
        CurrentValue += incrementValue;

        Progress = (float)Math.Clamp(CurrentValue / TargetValue, 0.0, 1.0);

        var activeMilestone = _milestones.FirstOrDefault(m => m.Status == CharacterGoalMilestoneStatus.Active);
        if (activeMilestone != null)
        {
            activeMilestone.RecordProgress(incrementValue, time);

            if (activeMilestone.Status == CharacterGoalMilestoneStatus.Completed)
            {
                var nextMilestone = _milestones
                    .Where(m => m.Order > activeMilestone.Order && m.Status == CharacterGoalMilestoneStatus.Pending)
                    .OrderBy(m => m.Order)
                    .FirstOrDefault();

                nextMilestone?.Activate();
            }
        }

        if (CurrentValue >= TargetValue)
        {
            Complete(time);
        }
        else
        {
            Version++;
            Touch();
        }
    }
}
