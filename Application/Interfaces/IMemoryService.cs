using Application.DTOs;
using Domain.Entities;

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
    /// </summary>
    Task<int> StoreCandidatesAsync(
        Guid userId,
        Guid characterId,
        Guid? sessionId,
        IEnumerable<MemoryCandidate> candidates,
        CancellationToken ct = default);
}
