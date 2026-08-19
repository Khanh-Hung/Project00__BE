using Domain.Enums;

namespace Application.DTOs;

public record ChatMessageDto(
    Guid Id,
    MessageRole Role,
    string Content,
    DateTime Timestamp
);

public record RelationshipEventDto(
    string EventKey,
    string Context,
    DateTime UnlockedAt
);

public record CharacterRelationshipDto(
    Guid Id,
    Guid CharacterId,
    Guid UserId,
    int AffectionScore,
    CharacterMood CurrentMood,
    int MoodIntensity,
    List<RelationshipEventDto> Events,
    DateTime LastInteractedAt
);

public record ChatSessionDto(
    Guid Id,
    Guid CharacterId,
    string CharacterName,
    string CharacterAvatar,
    string Title,
    List<ChatMessageDto> Messages,
    DateTime CreatedAt,
    string? CharacterTitle = null,
    string? CharacterPersonality = null,
    string? CharacterCategory = null,
    int AffectionScore = 0,
    int RelationshipLevel = 1,
    string? RelationshipStage = null,
    string? CurrentMood = null,
    int MoodIntensity = 20,
    List<RelationshipEventDto>? UnlockedEvents = null
);

public record ChatSessionListItemDto(
    Guid Id,
    Guid CharacterId,
    string CharacterName,
    string CharacterAvatar,
    string Title,
    string? LastMessageContent,
    DateTime? LastMessageTime,
    int MessageCount,
    DateTime CreatedAt,
    int AffectionScore = 0,
    int RelationshipLevel = 1,
    string? RelationshipStage = null
);

public record CreateSessionRequest(
    Guid CharacterId,
    string Title,
    Guid? UserId = null
);

public record SendMessageRequest(
    Guid SessionId,
    string Content
);

public record SendMessageResponse(
    ChatMessageDto UserMessage,
    ChatMessageDto AssistantMessage,
    int AffectionScore = 0,
    int RelationshipLevel = 1,
    string? RelationshipStage = null,
    string? CurrentMood = null,
    int MoodIntensity = 20,
    int AffectionDelta = 0,
    bool LevelUp = false,
    RelationshipEventDto? UnlockedEvent = null
);

public record RelationshipEventProposal(
    string Key,
    string Context
);

public record RoleplayTurnResult(
    string Reply,
    CharacterMood Mood = CharacterMood.Neutral,
    int MoodIntensity = 20,
    int AffectionDelta = 0,
    RelationshipEventProposal? Event = null
);

public record GenerateSceneImageRequest(
    Guid? SessionId,
    string? CharacterName,
    string? CharacterTitle,
    string? CharacterPersonality,
    string MessageContent,
    string? UserMessageContent = null
);
