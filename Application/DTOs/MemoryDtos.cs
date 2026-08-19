using Domain.Enums;

namespace Application.DTOs;

public record MemoryCandidate(
    string Content,
    MemoryType Type,
    int Importance = 3,
    decimal Confidence = 0.9m
);

public record MemoryExtractionResult(
    List<MemoryCandidate> Memories
);

public record MemoryExtractionJob(
    Guid SessionId,
    Guid CharacterId,
    Guid UserId,
    List<ChatMessageDto> RecentMessages
);

public record CharacterMemoryDto(
    Guid Id,
    string Content,
    MemoryType Type,
    int Importance,
    decimal Confidence,
    DateTime CreatedAt,
    DateTime? LastAccessedAt = null
);
