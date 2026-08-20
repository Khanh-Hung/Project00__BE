using Application.DTOs;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface IMemoryService
{
    /// <summary>
    /// Retrieves a diversity-balanced set of the most relevant memories for the (UserId, CharacterId) pair.
    /// Combines Semantic Vector Similarity (if queryText is provided) + Importance + Recency + Category Diversity up to maxCount.
    /// </summary>
    Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(
        Guid userId,
        Guid characterId,
        int maxCount = 6,
        string? queryText = null,
        CancellationToken ct = default);

    /// <summary>
    /// Validates, normalizes, deduplicates, and persists memory candidates.
    /// Automatically calculates and attaches vector embeddings for newly stored memories.
    /// </summary>
    Task<MemoryExtractionMetrics> StoreCandidatesAsync(
        Guid userId,
        Guid characterId,
        Guid? sessionId,
        IEnumerable<MemoryCandidate> candidates,
        CancellationToken ct = default);
}
