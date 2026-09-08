using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

public sealed class CharacterGoal : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public string Title { get; private set; }
    public string GoalKey => Title;
    public string? Description { get; private set; }
    public CharacterGoalType GoalType { get; private set; }
    public CharacterGoalStatus Status { get; private set; }
    public CharacterGoalPriority Priority { get; private set; }
    public float Progress { get; private set; }
    public double TargetValue { get; private set; }
    public double CurrentValue { get; private set; }
    public uint Version { get; private set; } = 1;

    public int ProgressPercentage => (int)Math.Round(Progress * 100.0f);

    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public DateTime? PausedAt { get; private set; }

    public DateTimeOffset CreatedAtUtc => new(CreatedAt, TimeSpan.Zero);
    public DateTimeOffset? UpdatedAtUtc => UpdatedAt.HasValue ? new(UpdatedAt.Value, TimeSpan.Zero) : null;
    public DateTimeOffset? CompletedAtUtc => CompletedAt.HasValue ? new(CompletedAt.Value, TimeSpan.Zero) : null;
    public DateTimeOffset? CancelledAtUtc => CancelledAt.HasValue ? new(CancelledAt.Value, TimeSpan.Zero) : null;

    private readonly List<CharacterGoalMilestone> _milestones = new();
    public IReadOnlyList<CharacterGoalMilestone> Milestones => _milestones.AsReadOnly();

    private CharacterGoal() : base() 
    {
        Title = null!;
    }

    public CharacterGoal(
        Guid characterId,
        string title,
        CharacterGoalType goalType = CharacterGoalType.PersonalGrowth,
        double targetValue = 100,
        CharacterGoalPriority priority = CharacterGoalPriority.Normal,
        string? description = null,
        CharacterGoalStatus initialStatus = CharacterGoalStatus.Active,
        Guid? id = null,
        DateTime? now = null,
        int initialProgress = 0) : base(id ?? Guid.CreateVersion7())
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty.", nameof(title));

        if (double.IsNaN(targetValue) || double.IsInfinity(targetValue) || targetValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetValue), "TargetValue must be a valid number greater than zero.");

        if (initialProgress < 0 || initialProgress > 100)
            throw new ArgumentOutOfRangeException(nameof(initialProgress), "Goal progress must be bounded in [0, 100].");

        var time = now ?? DateTime.UtcNow;

        CharacterId = characterId;
        Title = title.Trim();
        Description = description?.Trim();
        GoalType = goalType;
        Priority = priority;
        TargetValue = targetValue;
        Progress = (float)(initialProgress / 100.0);
        CurrentValue = (targetValue * initialProgress) / 100.0;
        Status = initialStatus;

        if (Status == CharacterGoalStatus.Active)
        {
            StartedAt = time;
        }

        if (initialProgress == 100)
        {
            Status = CharacterGoalStatus.Completed;
            CompletedAt = time;
        }
    }

    public static CharacterGoal Create(
        Guid characterId,
        string goalKey,
        CharacterGoalType goalType = CharacterGoalType.PersonalGrowth,
        int priority = (int)CharacterGoalPriority.Normal,
        string? description = null,
        CharacterGoalStatus initialStatus = CharacterGoalStatus.Active,
        int initialProgress = 0,
        Guid? id = null,
        DateTimeOffset? now = null)
    {
        var priorityEnum = priority switch
        {
            >= 75 => CharacterGoalPriority.Critical,
            >= 50 => CharacterGoalPriority.High,
            >= 25 => CharacterGoalPriority.Normal,
            _ => CharacterGoalPriority.Low
        };

        var time = now?.UtcDateTime ?? DateTime.UtcNow;
        return new CharacterGoal(
            characterId: characterId,
            title: goalKey,
            goalType: goalType,
            targetValue: 100,
            priority: priorityEnum,
            description: description,
            initialStatus: initialStatus,
            id: id,
            now: time,
            initialProgress: initialProgress);
    }

    public void Activate(DateTimeOffset now)
    {
        if (Status == CharacterGoalStatus.Completed || Status == CharacterGoalStatus.Cancelled || Status == CharacterGoalStatus.Expired)
            throw new InvalidOperationException($"Cannot activate goal in terminal state '{Status}'.");

        Status = CharacterGoalStatus.Active;
        StartedAt ??= now.UtcDateTime;
        PausedAt = null;
        Version++;
        Touch();
    }

    public void Activate(DateTime now) => Activate(new DateTimeOffset(now, TimeSpan.Zero));
    public void Activate() => Activate(DateTimeOffset.UtcNow);

    public void Pause(DateTimeOffset now)
    {
        if (Status != CharacterGoalStatus.Active)
            throw new InvalidOperationException($"Cannot pause a goal with status '{Status}'. Must be Active.");

        Status = CharacterGoalStatus.Paused;
        PausedAt = now.UtcDateTime;
        Version++;
        Touch();
    }

    public void Pause(DateTime now) => Pause(new DateTimeOffset(now, TimeSpan.Zero));
    public void Pause() => Pause(DateTimeOffset.UtcNow);

    public void Resume(DateTimeOffset now)
    {
        if (Status != CharacterGoalStatus.Paused)
            throw new InvalidOperationException($"Cannot resume a goal with status '{Status}'. Must be Paused.");

        Status = CharacterGoalStatus.Active;
        PausedAt = null;
        Version++;
        Touch();
    }

    public void Resume(DateTime now) => Resume(new DateTimeOffset(now, TimeSpan.Zero));
    public void Resume() => Resume(DateTimeOffset.UtcNow);

    public void Complete(DateTimeOffset now)
    {
        if (Status == CharacterGoalStatus.Completed)
            return;

        if (Status == CharacterGoalStatus.Draft)
            throw new InvalidOperationException("Draft goal cannot complete directly without becoming Active.");

        if (Status == CharacterGoalStatus.Paused)
            throw new InvalidOperationException("Paused goal cannot complete without Resume.");

        if (Status == CharacterGoalStatus.Cancelled || Status == CharacterGoalStatus.Expired)
            throw new InvalidOperationException($"Cannot complete goal in terminal state '{Status}'.");

        Status = CharacterGoalStatus.Completed;
        CompletedAt = now.UtcDateTime;
        Progress = 1.0f;
        CurrentValue = Math.Max(CurrentValue, TargetValue);

        Version++;
        Touch();
    }

    public void Complete(DateTime now) => Complete(new DateTimeOffset(now, TimeSpan.Zero));
    public void Complete() => Complete(DateTimeOffset.UtcNow);

    public void Cancel(DateTimeOffset now)
    {
        if (Status == CharacterGoalStatus.Cancelled)
            return;

        if (Status == CharacterGoalStatus.Completed || Status == CharacterGoalStatus.Expired)
            throw new InvalidOperationException($"Cannot cancel goal in terminal state '{Status}'.");

        Status = CharacterGoalStatus.Cancelled;
        CancelledAt = now.UtcDateTime;
        Version++;
        Touch();
    }

    public void Cancel(DateTime now) => Cancel(new DateTimeOffset(now, TimeSpan.Zero));
    public void Cancel() => Cancel(DateTimeOffset.UtcNow);

    public void Expire(DateTimeOffset now)
    {
        if (Status == CharacterGoalStatus.Completed)
            throw new InvalidOperationException("Completed goal cannot be expired.");

        if (Status == CharacterGoalStatus.Cancelled)
            throw new InvalidOperationException("Cancelled goal cannot be expired.");

        if (Status == CharacterGoalStatus.Expired)
            return;

        Status = CharacterGoalStatus.Expired;
        Version++;
        Touch();
    }

    public void Expire(DateTime now) => Expire(new DateTimeOffset(now, TimeSpan.Zero));
    public void Expire() => Expire(DateTimeOffset.UtcNow);

    /// <summary>
    /// Updates goal progress as an integer percentage [0, 100].
    /// Automatically marks the goal as Completed if percentage reaches 100.
    /// </summary>
    public void UpdateProgress(int percentage, DateTimeOffset now)
    {
        if (Status == CharacterGoalStatus.Completed || Status == CharacterGoalStatus.Cancelled || Status == CharacterGoalStatus.Expired)
            throw new InvalidOperationException($"Cannot update progress on goal in terminal state '{Status}'.");

        if (Status != CharacterGoalStatus.Active)
            throw new InvalidOperationException($"Cannot update progress on a goal with status '{Status}'. Must be Active.");

        if (percentage < 0 || percentage > 100)
            throw new ArgumentOutOfRangeException(nameof(percentage), "Progress percentage must be between 0 and 100.");

        Progress = (float)(percentage / 100.0);
        CurrentValue = (TargetValue * percentage) / 100.0;

        if (percentage >= 100)
        {
            Complete(now);
        }
        else
        {
            Version++;
            Touch();
        }
    }

    public void UpdateProgress(int percentage) => UpdateProgress(percentage, DateTimeOffset.UtcNow);

    public void UpdateProgress(int percentage, DateTime now) => UpdateProgress(percentage, new DateTimeOffset(now, TimeSpan.Zero));

    /// <summary>
    /// Legacy PR34 contribution API: adds raw domain contributionValue to CurrentValue,
    /// calculates Progress as CurrentValue / TargetValue, and propagates contribution into milestones.
    /// </summary>
    public void RecordProgress(double incrementValue, DateTimeOffset now)
    {
        if (Status != CharacterGoalStatus.Active)
            throw new InvalidOperationException($"Cannot record progress on a goal with status '{Status}'.");

        if (double.IsNaN(incrementValue) || double.IsInfinity(incrementValue) || incrementValue < 0)
            throw new ArgumentOutOfRangeException(nameof(incrementValue), "Progress increment must be a valid non-negative number.");

        var time = now.UtcDateTime;
        CurrentValue += incrementValue;
        Progress = (float)Math.Clamp(CurrentValue / TargetValue, 0.0, 1.0);

        // Cascading milestone progress allocation with overflow propagation (PR34 legacy logic preserved)
        double remainingForMilestones = incrementValue;
        while (remainingForMilestones > 0)
        {
            var activeMilestone = _milestones
                .Where(m => m.Status == CharacterGoalMilestoneStatus.Active)
                .OrderBy(m => m.Order)
                .FirstOrDefault();

            if (activeMilestone == null)
            {
                var nextPending = _milestones
                    .Where(m => m.Status == CharacterGoalMilestoneStatus.Pending)
                    .OrderBy(m => m.Order)
                    .FirstOrDefault();

                if (nextPending == null)
                    break;

                nextPending.Activate();
                activeMilestone = nextPending;
            }

            double needed = Math.Max(0, activeMilestone.TargetValue - activeMilestone.CurrentValue);
            if (needed <= 0)
            {
                activeMilestone.Complete(time);
                var nextPending = _milestones
                    .Where(m => m.Status == CharacterGoalMilestoneStatus.Pending)
                    .OrderBy(m => m.Order)
                    .FirstOrDefault();
                nextPending?.Activate();
                continue;
            }

            if (remainingForMilestones >= needed)
            {
                activeMilestone.RecordProgress(needed, time);
                remainingForMilestones -= needed;
                
                var nextPending = _milestones
                    .Where(m => m.Status == CharacterGoalMilestoneStatus.Pending)
                    .OrderBy(m => m.Order)
                    .FirstOrDefault();
                nextPending?.Activate();
            }
            else
            {
                activeMilestone.RecordProgress(remainingForMilestones, time);
                remainingForMilestones = 0;
            }
        }

        if (CurrentValue >= TargetValue)
        {
            Complete(now);
        }
        else
        {
            Version++;
            Touch();
        }
    }

    public void RecordProgress(double incrementValue, DateTime now) =>
        RecordProgress(incrementValue, new DateTimeOffset(now, TimeSpan.Zero));

    public void RecordProgress(double incrementValue) =>
        RecordProgress(incrementValue, DateTimeOffset.UtcNow);

    public CharacterGoalMilestone AddMilestone(string title, int order, double targetValue, string? description = null)
    {
        if (double.IsNaN(targetValue) || double.IsInfinity(targetValue) || targetValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetValue), "TargetValue must be a valid number greater than zero.");

        if (_milestones.Any(m => m.Order == order))
            throw new ArgumentException($"Milestone with order {order} already exists.", nameof(order));

        var currentTotalMilestones = _milestones.Sum(m => m.TargetValue);
        if (currentTotalMilestones + targetValue > TargetValue)
            throw new InvalidOperationException($"Total milestone target ({currentTotalMilestones + targetValue}) cannot exceed goal TargetValue ({TargetValue}).");

        var milestone = new CharacterGoalMilestone(Id, title, order, targetValue, description);
        _milestones.Add(milestone);
        _milestones.Sort((a, b) => a.Order.CompareTo(b.Order));

        if (!_milestones.Any(m => m.Status == CharacterGoalMilestoneStatus.Active || m.Status == CharacterGoalMilestoneStatus.Completed))
        {
            var firstMilestone = _milestones.First();
            firstMilestone.Activate();
        }

        Version++;
        Touch();
        return milestone;
    }
}
