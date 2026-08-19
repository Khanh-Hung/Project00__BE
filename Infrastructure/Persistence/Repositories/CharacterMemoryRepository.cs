using Application.Abstractions.Data;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public sealed class CharacterMemoryRepository : GenericRepository<CharacterMemory>, ICharacterMemoryRepository
{
    public CharacterMemoryRepository(ProjectDbContext dbContext) : base(dbContext)
    {
    }

    public async Task<IReadOnlyList<CharacterMemory>> GetTopImportantAsync(
        Guid userId,
        Guid characterId,
        int minImportance = 3,
        int limit = 20,
        CancellationToken ct = default)
    {
        return await DbContext.CharacterMemories
            .Where(m => m.UserId == userId &&
                        m.CharacterId == characterId &&
                        m.Importance >= minImportance &&
                        !m.IsSoftDeleted)
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CharacterMemory>> GetMostRecentAsync(
        Guid userId,
        Guid characterId,
        int limit = 20,
        CancellationToken ct = default)
    {
        return await DbContext.CharacterMemories
            .Where(m => m.UserId == userId &&
                        m.CharacterId == characterId &&
                        !m.IsSoftDeleted)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CharacterMemory>> GetExistingByTypesAsync(
        Guid userId,
        Guid characterId,
        IEnumerable<MemoryType> types,
        CancellationToken ct = default)
    {
        var typeList = types.ToList();
        if (typeList.Count == 0)
        {
            return Array.Empty<CharacterMemory>();
        }

        return await DbContext.CharacterMemories
            .Where(m => m.UserId == userId &&
                        m.CharacterId == characterId &&
                        typeList.Contains(m.Type) &&
                        !m.IsSoftDeleted)
            .ToListAsync(ct);
    }
}
