using Domain.Enums;

namespace Application.DTOs;

public record ChatMessageDto(
    Guid Id,
    MessageRole Role,
    string Content,
    DateTime Timestamp
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
    string? CurrentMood = null
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
    int RelationshipLevel = 1
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
    string? CurrentMood = null,
    int AffectionDelta = 0,
    bool LevelUp = false
);

public record RoleplayTurnResult(
    string Reply,
    string? Mood = null,
    int AffectionDelta = 2
);

public record GenerateSceneImageRequest(
    Guid? SessionId,
    string? CharacterName,
    string? CharacterTitle,
    string? CharacterPersonality,
    string MessageContent,
    string? UserMessageContent = null
);
