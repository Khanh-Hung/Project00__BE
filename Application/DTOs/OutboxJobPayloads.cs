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
    string RawText,
    Guid SessionId = default
);

/// <summary>
/// Scene Image Generation payload carrying the immutable visual snapshot of Turn N as the single source of truth.
/// Outbox workers compile deterministic prompts directly from this snapshot without querying current database session state.
/// </summary>
public sealed record SceneImageGenerationOutboxPayload(
    Guid TurnId,
    Guid CharacterId,
    Guid UserId,
    VisualSnapshot Snapshot,
    Guid GenerationRequestId = default
)
{
    public Guid ResolvedGenerationRequestId => GenerationRequestId != default ? GenerationRequestId : (Snapshot != null ? Snapshot.TurnId : Guid.NewGuid());
}

public sealed record MemoryExtractionOutboxPayload(
    Guid SessionId,
    Guid CharacterId,
    Guid UserId,
    IReadOnlyList<ChatMessageDto> RecentMessages,
    int UserMessageCount
);
