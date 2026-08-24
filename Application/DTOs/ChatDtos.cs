using Domain.Enums;

namespace Application.DTOs;

public static class SceneImageStatuses
{
    public const string Queued = "queued";
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static string FromJobStatus(ImageJobStatus status) => status switch
    {
        ImageJobStatus.Pending => Pending,
        ImageJobStatus.Processing => Processing,
        ImageJobStatus.Completed => Completed,
        ImageJobStatus.Failed => Failed,
        _ => Pending
    };
}

public record ChatMessageDto(
    Guid Id,
    MessageRole Role,
    string Content,
    DateTime Timestamp,
    Guid? TurnId = null,
    string? SceneImageUrl = null,
    string? SceneImageStatus = null, // BE hydration: "pending", "processing", "completed", "failed"; FE optimistic/trigger: "queued"
    Guid? GenerationRequestId = null
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
    DateTime LastInteractedAt,
    string? RelationshipStage = null
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
    List<RelationshipEventDto>? UnlockedEvents = null,
    SessionStatus Status = SessionStatus.Active,
    string? WalkOutReason = null,
    DateTime? WalkedOutAt = null
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
    string? RelationshipStage = null,
    SessionStatus Status = SessionStatus.Active,
    string? WalkOutReason = null
);

public record CreateSessionRequest(
    Guid CharacterId,
    string Title,
    Guid? UserId = null
);

public record SendMessageRequest(
    Guid SessionId,
    string Content,
    Guid? TurnId = null
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
    RelationshipEventDto? UnlockedEvent = null,
    bool HasWalkedOut = false,
    string? WalkOutReason = null,
    SessionStatus SessionStatus = SessionStatus.Active,
    Guid? TurnId = null
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
    RelationshipEventProposal? Event = null,
    bool HasWalkedOut = false,
    string? WalkOutReason = null
);

public record GenerateSceneImageRequest(
    Guid? SessionId,
    string? CharacterName,
    string? CharacterTitle,
    string? CharacterPersonality,
    string MessageContent,
    string? UserMessageContent = null,
    string? ReferenceImageUrl = null,
    Domain.ValueObjects.CharacterVisualIdentity? VisualIdentity = null,
    string? WorldDescription = null,
    Domain.ValueObjects.SessionSceneState? SceneState = null
);

public record RegenerateResponse(
    ChatMessageDto NewAssistantMessage,
    int AffectionScore = 0,
    int RelationshipLevel = 1,
    string? RelationshipStage = null,
    string? CurrentMood = null,
    int MoodIntensity = 20,
    int AffectionDelta = 0,
    bool LevelUp = false,
    RelationshipEventDto? UnlockedEvent = null
);

public record TriggerSceneImageResponse(
    Guid GenerationRequestId,
    Guid TurnId,
    string Status = "queued"
);

public record SceneImageStatusResponse(
    Guid GenerationRequestId,
    Guid TurnId,
    Guid SessionId,
    string Status, // "queued", "pending", "processing", "completed", "failed", "cancelled"
    string? ImageUrl = null,
    string? FailureReason = null,
    bool? IsRetryable = null,
    int? SceneRevision = null,
    string? Prompt = null,
    DateTime? CreatedAt = null
);

public record SceneImageDto(
    Guid Id,
    Guid SessionId,
    Guid CharacterId,
    Guid TurnId,
    int SceneRevision,
    Guid GenerationRequestId,
    string ImageUrl,
    string Prompt,
    bool IsCurrent,
    DateTime CreatedAt
);
