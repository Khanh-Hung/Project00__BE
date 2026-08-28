using Domain.Common;

namespace Domain.Entities;

public sealed class CharacterTurn : BaseEntity
{
    public Guid TurnId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid CharacterId { get; private set; }
    public Guid UserMessageId { get; private set; }
    public Guid AssistantMessageId { get; private set; }
    public string UserMessage { get; private set; } = string.Empty;
    public string AssistantReply { get; private set; } = string.Empty;
    public string Mood { get; private set; } = "Neutral";
    public int MoodIntensity { get; private set; } = 50;
    public int AffectionDelta { get; private set; }
    public int AffectionScore { get; private set; }
    public string RelationshipStage { get; private set; } = "Stranger";
    public Guid RelationshipId { get; private set; }
    public DateTime LastInteractedAt { get; private set; }
    public string? EventsJson { get; private set; }
    public string? ActiveMemoriesJson { get; private set; }
    public string? VisualSnapshotJson { get; private set; }

    private CharacterTurn() { } // EF Core

    public CharacterTurn(
        Guid turnId,
        Guid sessionId,
        Guid userId,
        Guid characterId,
        Guid userMessageId,
        Guid assistantMessageId,
        string userMessage,
        string assistantReply,
        string mood,
        int moodIntensity,
        int affectionDelta,
        int affectionScore,
        string relationshipStage,
        Guid relationshipId = default,
        DateTime lastInteractedAt = default,
        string? eventsJson = null,
        string? activeMemoriesJson = null,
        string? visualSnapshotJson = null)
    {
        TurnId = turnId;
        SessionId = sessionId;
        UserId = userId;
        CharacterId = characterId;
        UserMessageId = userMessageId;
        AssistantMessageId = assistantMessageId;
        UserMessage = userMessage;
        AssistantReply = assistantReply;
        Mood = mood;
        MoodIntensity = moodIntensity;
        AffectionDelta = affectionDelta;
        AffectionScore = affectionScore;
        RelationshipStage = relationshipStage;
        RelationshipId = relationshipId;
        LastInteractedAt = lastInteractedAt;
        EventsJson = eventsJson;
        ActiveMemoriesJson = activeMemoriesJson;
        VisualSnapshotJson = visualSnapshotJson;
    }
}
