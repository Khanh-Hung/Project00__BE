using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Domain entity representing a processed reaction to a specific CharacterWorldEvent.
/// Provides database-level idempotency protection via unique constraint on (WorldEventId, CharacterId).
/// </summary>
public sealed class CharacterWorldEventReaction : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid WorldEventId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public PerceptionType PerceptionType { get; private set; }
    public ReactionPriority Priority { get; private set; }
    public string? ReactionReason { get; private set; }
    public int MoodDelta { get; private set; }
    public int EnergyDelta { get; private set; }
    public int StressDelta { get; private set; }
    public int HungerDelta { get; private set; }
    public int SocialNeedDelta { get; private set; }
    public int ConfidenceDelta { get; private set; }
    public int RelationshipDelta { get; private set; }
    public Guid? GoalId { get; private set; }
    public double? GoalContribution { get; private set; }
    public Guid? MemoryId { get; private set; }
    public bool ActivityTriggered { get; private set; }
    public CharacterActivityType? TriggeredActivityType { get; private set; }
    public bool VisualMomentCreated { get; private set; }
    public Guid? SceneIntentId { get; private set; }
    public Guid? SceneSpecificationId { get; private set; }
    public DateTime ProcessedAt { get; private set; }

    private CharacterWorldEventReaction() { } // EF Core

    private CharacterWorldEventReaction(
        Guid id,
        Guid characterId,
        Guid worldEventId,
        Guid executionId,
        PerceptionType perceptionType,
        ReactionPriority priority,
        string? reactionReason,
        int moodDelta,
        int energyDelta,
        int stressDelta,
        int hungerDelta,
        int socialNeedDelta,
        int confidenceDelta,
        int relationshipDelta,
        Guid? goalId,
        double? goalContribution,
        Guid? memoryId,
        bool activityTriggered,
        CharacterActivityType? triggeredActivityType,
        bool visualMomentCreated,
        Guid? sceneIntentId,
        Guid? sceneSpecificationId,
        DateTime processedAt)
    {
        Id = id;
        CharacterId = characterId;
        WorldEventId = worldEventId;
        ExecutionId = executionId;
        PerceptionType = perceptionType;
        Priority = priority;
        ReactionReason = reactionReason;
        MoodDelta = moodDelta;
        EnergyDelta = energyDelta;
        StressDelta = stressDelta;
        HungerDelta = hungerDelta;
        SocialNeedDelta = socialNeedDelta;
        ConfidenceDelta = confidenceDelta;
        RelationshipDelta = relationshipDelta;
        GoalId = goalId;
        GoalContribution = goalContribution;
        MemoryId = memoryId;
        ActivityTriggered = activityTriggered;
        TriggeredActivityType = triggeredActivityType;
        VisualMomentCreated = visualMomentCreated;
        SceneIntentId = sceneIntentId;
        SceneSpecificationId = sceneSpecificationId;
        ProcessedAt = processedAt;
    }

    public static CharacterWorldEventReaction Create(
        Guid characterId,
        Guid worldEventId,
        Guid executionId,
        PerceptionType perceptionType,
        ReactionPriority priority,
        string? reactionReason = null,
        int moodDelta = 0,
        int energyDelta = 0,
        int stressDelta = 0,
        int hungerDelta = 0,
        int socialNeedDelta = 0,
        int confidenceDelta = 0,
        int relationshipDelta = 0,
        Guid? goalId = null,
        double? goalContribution = null,
        Guid? memoryId = null,
        bool activityTriggered = false,
        CharacterActivityType? triggeredActivityType = null,
        bool visualMomentCreated = false,
        Guid? sceneIntentId = null,
        Guid? sceneSpecificationId = null,
        DateTime? processedAt = null,
        Guid? id = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (worldEventId == Guid.Empty)
            throw new ArgumentException("WorldEventId cannot be empty.", nameof(worldEventId));

        if (executionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));

        var reactionId = id ?? Guid.CreateVersion7();
        var time = processedAt ?? DateTime.UtcNow;

        return new CharacterWorldEventReaction(
            id: reactionId,
            characterId: characterId,
            worldEventId: worldEventId,
            executionId: executionId,
            perceptionType: perceptionType,
            priority: priority,
            reactionReason: reactionReason,
            moodDelta: moodDelta,
            energyDelta: energyDelta,
            stressDelta: stressDelta,
            hungerDelta: hungerDelta,
            socialNeedDelta: socialNeedDelta,
            confidenceDelta: confidenceDelta,
            relationshipDelta: relationshipDelta,
            goalId: goalId,
            goalContribution: goalContribution,
            memoryId: memoryId,
            activityTriggered: activityTriggered,
            triggeredActivityType: triggeredActivityType,
            visualMomentCreated: visualMomentCreated,
            sceneIntentId: sceneIntentId,
            sceneSpecificationId: sceneSpecificationId,
            processedAt: time
        );
    }
}
