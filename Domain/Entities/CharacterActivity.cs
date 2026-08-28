using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Authoritative domain entity representing an activity performed by a character.
/// Tracks autonomous vs user-directed lifecycle, schedule, visual moment eligibility, and execution state.
/// </summary>
public sealed class CharacterActivity : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public CharacterActivityType ActivityType { get; private set; }
    public CharacterActivityStatus Status { get; private set; }
    public CharacterActivitySource Source { get; private set; }
    public string Location { get; private set; }
    public ActivityPriority Priority { get; private set; }
    public int DurationMinutes { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public bool ShouldCreateVisualMoment { get; private set; }
    public Guid? SceneIntentId { get; private set; }
    public string TimeBucket { get; private set; }
    public string DecisionFingerprint { get; private set; }
    public string? Reason { get; private set; }
    public uint Version { get; private set; } = 1;

    private CharacterActivity() 
    {
        Location = null!;
        TimeBucket = null!;
        DecisionFingerprint = null!;
    } // EF Core

    public CharacterActivity(
        Guid characterId,
        CharacterActivityType activityType,
        string location,
        string timeBucket,
        string decisionFingerprint,
        CharacterActivitySource source = CharacterActivitySource.Autonomous,
        ActivityPriority priority = ActivityPriority.Normal,
        int durationMinutes = 30,
        bool shouldCreateVisualMoment = false,
        string? reason = null,
        DateTime? startedAt = null,
        DateTime? completedAt = null,
        Guid? sceneIntentId = null,
        CharacterActivityStatus status = CharacterActivityStatus.Scheduled,
        uint version = 1,
        DateTime? now = null,
        Guid? id = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(location))
            throw new ArgumentException("Location cannot be empty.", nameof(location));

        if (string.IsNullOrWhiteSpace(timeBucket))
            throw new ArgumentException("TimeBucket cannot be empty.", nameof(timeBucket));

        if (string.IsNullOrWhiteSpace(decisionFingerprint))
            throw new ArgumentException("DecisionFingerprint cannot be empty.", nameof(decisionFingerprint));

        if (durationMinutes < 1)
            throw new ArgumentOutOfRangeException(nameof(durationMinutes), "DurationMinutes must be >= 1.");

        Id = id ?? Guid.CreateVersion7();
        CharacterId = characterId;
        ActivityType = activityType;
        Location = location.Trim();
        TimeBucket = timeBucket.Trim();
        DecisionFingerprint = decisionFingerprint.Trim();
        Source = source;
        Priority = priority;
        DurationMinutes = durationMinutes;
        ShouldCreateVisualMoment = shouldCreateVisualMoment;
        Reason = reason?.Trim();
        StartedAt = startedAt;
        CompletedAt = completedAt;
        SceneIntentId = sceneIntentId;
        Status = status;
        Version = version;
        CreatedAt = now ?? DateTime.UtcNow;
    }

    public void Start(DateTime? startedAt = null)
    {
        if (Status == CharacterActivityStatus.Cancelled || Status == CharacterActivityStatus.Completed)
            throw new InvalidOperationException($"Cannot start activity in '{Status}' status.");

        Status = CharacterActivityStatus.Started;
        StartedAt = startedAt ?? DateTime.UtcNow;
        Version++;
        Touch();
    }

    public void Complete(DateTime? completedAt = null)
    {
        if (Status == CharacterActivityStatus.Cancelled)
            throw new InvalidOperationException("Cannot complete a cancelled activity.");

        Status = CharacterActivityStatus.Completed;
        CompletedAt = completedAt ?? DateTime.UtcNow;
        Version++;
        Touch();
    }

    public void Cancel(string reason, DateTime? cancelledAt = null)
    {
        if (Status == CharacterActivityStatus.Completed)
            throw new InvalidOperationException("Cannot cancel an already completed activity.");

        Status = CharacterActivityStatus.Cancelled;
        Reason = string.IsNullOrWhiteSpace(Reason) ? $"Cancelled: {reason}" : $"{Reason} | Cancelled: {reason}";
        CompletedAt = cancelledAt ?? DateTime.UtcNow;
        Version++;
        Touch();
    }

    public void LinkSceneIntent(Guid sceneIntentId)
    {
        if (sceneIntentId == Guid.Empty)
            throw new ArgumentException("SceneIntentId cannot be empty.", nameof(sceneIntentId));

        SceneIntentId = sceneIntentId;
        ShouldCreateVisualMoment = true;
        Version++;
        Touch();
    }
}
