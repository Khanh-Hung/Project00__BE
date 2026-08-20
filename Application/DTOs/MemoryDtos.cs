using Domain.Enums;
using Domain.ValueObjects;

namespace Application.DTOs;

public record MemoryExtractionResult(
    List<MemoryCandidate> Memories
);

public record MemoryExtractionJob(
    Guid SessionId,
    Guid CharacterId,
    Guid UserId,
    List<ChatMessageDto> RecentMessages,
    int UserMessageCount = 0
);

public class MemoryExtractionOptions
{
    public int BatchSize { get; set; } = 5;
    public int WindowSize { get; set; } = 10;
    public decimal MinConfidence { get; set; } = 0.60m;
    public int MaxCandidates { get; set; } = 3;
    public int QueueCapacity { get; set; } = 100;
}

public record MemoryExtractionMetrics(
    int ExtractedCount,
    int AcceptedCount,
    int RejectedCount,
    int DuplicateCount,
    int PersistedCount
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
