using Domain.Entities;

namespace Application.Interfaces;

public interface IVisualMemoryReader
{
    Task<IReadOnlyList<CharacterVisualMemory>> GetRelevantMemoriesAsync(
        Guid characterId,
        string? locationContext = null,
        int maxResults = 3,
        CancellationToken ct = default);

    Task<CharacterVisualMemory?> GetLatestMemoryAsync(
        Guid characterId,
        CancellationToken ct = default);
}
