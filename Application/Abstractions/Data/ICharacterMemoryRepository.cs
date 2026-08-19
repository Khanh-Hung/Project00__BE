using Domain.Entities;
using Domain.Enums;

namespace Application.Abstractions.Data;

public interface ICharacterMemoryRepository : IGenericRepository<CharacterMemory>
{
    Task<IReadOnlyList<CharacterMemory>> GetTopImportantAsync(
        Guid userId,
        Guid characterId,
        int minImportance = 3,
        int limit = 20,
        CancellationToken ct = default);

    Task<IReadOnlyList<CharacterMemory>> GetMostRecentAsync(
        Guid userId,
        Guid characterId,
        int limit = 20,
        CancellationToken ct = default);

    Task<IReadOnlyList<CharacterMemory>> GetExistingByTypesAsync(
        Guid userId,
        Guid characterId,
        IEnumerable<MemoryType> types,
        CancellationToken ct = default);
}
