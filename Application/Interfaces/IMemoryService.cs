using Application.DTOs;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface IMemoryService
{
    /// <summary>
    /// Retrieves a diversity-balanced set of the most relevant memories for the (UserId, CharacterId) pair.
    /// Combines Importance + Recency + Category Diversity up to maxCount.
    /// </summary>
    Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(
        Guid userId,
        Guid characterId,
        int maxCount = 6,
        CancellationToken ct = default);

    /// <summary>
    /// Validates, normalizes, deduplicates, and persists memory candidates.
    /// Returns detailed metrics on accepted, rejected, duplicate, and persisted counts.
    /// </summary>
    Task<MemoryExtractionMetrics> StoreCandidatesAsync(
        Guid userId,
        Guid characterId,
        Guid? sessionId,
        IEnumerable<MemoryCandidate> candidates,
        CancellationToken ct = default);
}
