using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Domain aggregate representing a discrete autonomous execution tick for a character.
/// Provides database-level idempotency protection via unique constraint on (CharacterId, TimeBucket).
/// Enforces strict terminal state machine transitions: Running -> Completed | Failed, with controlled retry on Failed.
/// </summary>
public sealed class CharacterAutonomyTick : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public string TimeBucket { get; private set; } = string.Empty;
    public AutonomyTickStatus Status { get; private set; }
    public Guid? WorldEventId { get; private set; }
    public Guid? ReactionId { get; private set; }
    public Guid? ActivityId { get; private set; }
    public Guid? SceneSpecificationId { get; private set; }
    public string? DecisionFingerprint { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? FailedAt { get; private set; }
    public string? ErrorMessage { get; private set; }
    public long Version { get; private set; } = 1;

    private CharacterAutonomyTick() { } // EF Core

    private CharacterAutonomyTick(
        Guid id,
        Guid characterId,
        Guid executionId,
        string timeBucket,
        AutonomyTickStatus status,
        DateTime startedAt,
        Guid? worldEventId = null,
        string? correlationId = null)
    {
        Id = id;
        CharacterId = characterId;
        ExecutionId = executionId;
        TimeBucket = timeBucket;
        Status = status;
        StartedAt = startedAt;
        WorldEventId = worldEventId;
        CorrelationId = correlationId;
    }

    public static CharacterAutonomyTick Create(
        Guid characterId,
        Guid executionId,
        string timeBucket,
        DateTime? startedAt = null,
        Guid? worldEventId = null,
        string? correlationId = null,
        Guid? id = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (executionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));

        if (string.IsNullOrWhiteSpace(timeBucket))
            throw new ArgumentException("TimeBucket cannot be empty.", nameof(timeBucket));

        var tickId = id ?? Guid.CreateVersion7();
        var startTime = startedAt ?? DateTime.UtcNow;

        return new CharacterAutonomyTick(
            id: tickId,
            characterId: characterId,
            executionId: executionId,
            timeBucket: timeBucket.Trim(),
            status: AutonomyTickStatus.Running,
            startedAt: startTime,
            worldEventId: worldEventId,
            correlationId: correlationId?.Trim()
        );
    }

    public void LinkReaction(Guid reactionId)
    {
        if (Status != AutonomyTickStatus.Running)
            throw new InvalidOperationException($"Cannot link reaction to tick in status {Status}. Only Running ticks can link reactions.");

        ReactionId = reactionId;
        Touch();
    }

    public void Complete(
        DateTime completedAt,
        Guid? activityId = null,
        Guid? sceneSpecificationId = null,
        string? decisionFingerprint = null)
    {
        if (Status != AutonomyTickStatus.Running)
            throw new InvalidOperationException($"Cannot complete tick in status {Status}. Only Running ticks can be transitioned to Completed.");

        Status = AutonomyTickStatus.Completed;
        CompletedAt = completedAt;
        ActivityId = activityId;
        SceneSpecificationId = sceneSpecificationId;
        DecisionFingerprint = decisionFingerprint;
        ErrorMessage = null;
        Version++;
        Touch();
    }

    public void Fail(DateTime failedAt, string errorMessage)
    {
        if (Status != AutonomyTickStatus.Running)
            throw new InvalidOperationException($"Cannot fail tick in status {Status}. Only Running ticks can be transitioned to Failed.");

        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        Status = AutonomyTickStatus.Failed;
        FailedAt = failedAt;
        ErrorMessage = errorMessage.Length > 1024 ? errorMessage.Substring(0, 1024) : errorMessage;
        Version++;
        Touch();
    }

    public void ReclaimForRetry(
        Guid newExecutionId,
        DateTime restartedAt,
        Guid? newWorldEventId = null,
        string? newCorrelationId = null)
    {
        if (Status != AutonomyTickStatus.Failed)
            throw new InvalidOperationException($"Cannot reclaim tick in status {Status}. Only Failed ticks can be reclaimed for controlled retry.");

        if (newExecutionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(newExecutionId));

        Status = AutonomyTickStatus.Running;
        ExecutionId = newExecutionId;
        StartedAt = restartedAt;
        FailedAt = null;
        CompletedAt = null;
        ErrorMessage = null;
        ReactionId = null;
        ActivityId = null;
        SceneSpecificationId = null;
        DecisionFingerprint = null;
        if (newWorldEventId.HasValue) WorldEventId = newWorldEventId.Value;
        if (!string.IsNullOrWhiteSpace(newCorrelationId)) CorrelationId = newCorrelationId.Trim();
        Version++;
        Touch();
    }
}
