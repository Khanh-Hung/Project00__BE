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
        Progress = initialProgress;
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

    public void Activate(DateTimeOffset? now = null)
    {
        if (Status == CharacterGoalStatus.Completed || Status == CharacterGoalStatus.Cancelled || Status == CharacterGoalStatus.Expired)
            throw new InvalidOperationException($"Cannot activate goal in terminal state '{Status}'.");

        Status = CharacterGoalStatus.Active;
        StartedAt ??= now?.UtcDateTime ?? DateTime.UtcNow;
        PausedAt = null;
        Version++;
        Touch();
    }

    public void Activate(DateTime now) => Activate((DateTimeOffset)now);

    public void Pause(DateTimeOffset? now = null)
    {
        if (Status != CharacterGoalStatus.Active)
            throw new InvalidOperationException($"Cannot pause a goal with status '{Status}'. Must be Active.");

        Status = CharacterGoalStatus.Paused;
        PausedAt = now?.UtcDateTime ?? DateTime.UtcNow;
        Version++;
        Touch();
    }

    public void Pause(DateTime now) => Pause((DateTimeOffset)now);

    public void Resume(DateTimeOffset? now = null)
    {
        if (Status != CharacterGoalStatus.Paused)
            throw new InvalidOperationException($"Cannot resume a goal with status '{Status}'. Must be Paused.");

        Status = CharacterGoalStatus.Active;
        PausedAt = null;
        Version++;
        Touch();
    }

    public void Resume(DateTime now) => Resume((DateTimeOffset)now);

    public void Complete(DateTimeOffset? now = null)
    {
        if (Status == CharacterGoalStatus.Completed)
            return;

        if (Status == CharacterGoalStatus.Scheduled)
            throw new InvalidOperationException("Scheduled goal cannot complete directly without becoming Active.");

        if (Status == CharacterGoalStatus.Paused)
            throw new InvalidOperationException("Paused goal cannot complete without Resume.");

        if (Status == CharacterGoalStatus.Cancelled || Status == CharacterGoalStatus.Expired)
            throw new InvalidOperationException($"Cannot complete goal in terminal state '{Status}'.");

        Status = CharacterGoalStatus.Completed;
        CompletedAt = now?.UtcDateTime ?? DateTime.UtcNow;
        Progress = 100f;
        CurrentValue = Math.Max(CurrentValue, TargetValue);

        Version++;
        Touch();
    }

    public void Complete(DateTime now) => Complete((DateTimeOffset)now);

    public void Cancel(DateTimeOffset? now = null)
    {
        if (Status == CharacterGoalStatus.Cancelled)
            return;

        if (Status == CharacterGoalStatus.Completed || Status == CharacterGoalStatus.Expired)
            throw new InvalidOperationException($"Cannot cancel goal in terminal state '{Status}'.");

        Status = CharacterGoalStatus.Cancelled;
        CancelledAt = now?.UtcDateTime ?? DateTime.UtcNow;
        Version++;
        Touch();
    }

    public void Cancel(DateTime now) => Cancel((DateTimeOffset)now);

    public void Expire(DateTimeOffset? now = null)
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

    public void Expire(DateTime now) => Expire((DateTimeOffset)now);

    public void UpdateProgress(int newProgress, DateTimeOffset? now = null)
    {
        if (Status == CharacterGoalStatus.Completed || Status == CharacterGoalStatus.Cancelled || Status == CharacterGoalStatus.Expired)
            throw new InvalidOperationException($"Cannot update progress on goal in terminal state '{Status}'.");

        if (newProgress < 0 || newProgress > 100)
            throw new ArgumentOutOfRangeException(nameof(newProgress), "Progress must be between 0 and 100.");

        var time = now?.UtcDateTime ?? DateTime.UtcNow;
        Progress = newProgress;
        CurrentValue = (TargetValue * newProgress) / 100.0;

        if (newProgress >= 100)
        {
            Complete(time);
        }
        else
        {
            Version++;
            Touch();
        }
    }

    public void UpdateProgress(int newProgress, DateTime now) => UpdateProgress(newProgress, (DateTimeOffset)now);

    public void SetProgress(int progress, DateTimeOffset? now = null) => UpdateProgress(progress, now);
    public void SetProgress(int progress, DateTime now) => UpdateProgress(progress, (DateTimeOffset)now);

    public void RecordProgress(int increment, DateTimeOffset? now = null)
    {
        if (Status == CharacterGoalStatus.Completed || Status == CharacterGoalStatus.Cancelled || Status == CharacterGoalStatus.Expired)
            throw new InvalidOperationException($"Cannot record progress on goal in terminal state '{Status}'.");

        if (Status != CharacterGoalStatus.Active)
            throw new InvalidOperationException($"Cannot record progress on a goal with status '{Status}'. Must be Active.");

        if (increment < 0)
            throw new ArgumentOutOfRangeException(nameof(increment), "Progress increment must be non-negative.");

        var newProgress = (int)Progress + increment;
        if (newProgress > 100)
            throw new ArgumentOutOfRangeException(nameof(increment), "Progress cannot exceed 100.");

        UpdateProgress(newProgress, now);
    }

    public void RecordProgress(int increment, DateTime now) => RecordProgress(increment, (DateTimeOffset)now);

    public void RecordProgress(double incrementValue, DateTimeOffset? now = null)
    {
        if (double.IsNaN(incrementValue) || double.IsInfinity(incrementValue) || incrementValue < 0)
            throw new ArgumentOutOfRangeException(nameof(incrementValue), "Progress increment must be a valid non-negative number.");

        RecordProgress((int)Math.Round(incrementValue), now);
    }

    public void RecordProgress(double incrementValue, DateTime now) => RecordProgress(incrementValue, (DateTimeOffset)now);

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
