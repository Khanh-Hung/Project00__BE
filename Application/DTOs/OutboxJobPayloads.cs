using Domain.Enums;
using Domain.ValueObjects;

namespace Application.DTOs;

public static class OutboxEventTypes
{
    public const string VoiceGeneration = "VoiceGeneration";
    public const string SceneImageGeneration = "SceneImageGeneration";
    public const string MemoryExtraction = "MemoryExtraction";
}

public sealed record VoiceGenerationOutboxPayload(
    Guid TurnId,
    Guid CharacterId,
    Guid UserId,
    CharacterVoiceProfile VoiceProfile,
    CharacterMood Mood,
    int MoodIntensity,
    int AffectionScore,
    string RelationshipStage,
    string RawText
);

public sealed record SceneImageGenerationOutboxPayload(
    Guid TurnId,
    Guid CharacterId,
    Guid UserId,
    string CharacterTitle,
    string Mood,
    string Prompt
);

public sealed record MemoryExtractionOutboxPayload(
    Guid SessionId,
    Guid CharacterId,
    Guid UserId,
    IReadOnlyList<ChatMessageDto> RecentMessages,
    int UserMessageCount
);
