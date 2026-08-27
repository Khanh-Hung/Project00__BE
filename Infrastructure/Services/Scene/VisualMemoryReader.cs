using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.Scene;

public sealed class VisualMemoryReader : IVisualMemoryReader
{
    private readonly ProjectDbContext _dbContext;

    public VisualMemoryReader(ProjectDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<CharacterVisualMemory>> GetRelevantMemoriesAsync(
        Guid characterId,
        string? locationContext = null,
        int maxResults = 3,
        CancellationToken ct = default)
    {
        var query = _dbContext.CharacterVisualMemories
            .AsNoTracking()
            .Where(m => m.CharacterId == characterId);

        if (!string.IsNullOrWhiteSpace(locationContext))
        {
            var loc = locationContext.Trim();
            query = query.OrderByDescending(m => m.Context != null && m.Context.Contains(loc))
                .ThenByDescending(m => m.IdentityScore ?? 0.5f)
                .ThenByDescending(m => m.SceneRevision);
        }
        else
        {
            query = query.OrderByDescending(m => m.IdentityScore ?? 0.5f)
                .ThenByDescending(m => m.SceneRevision);
        }

        return await query.Take(maxResults).ToListAsync(ct);
    }

    public async Task<CharacterVisualMemory?> GetLatestMemoryAsync(
        Guid characterId,
        CancellationToken ct = default)
    {
        return await _dbContext.CharacterVisualMemories
            .AsNoTracking()
            .Where(m => m.CharacterId == characterId)
            .OrderByDescending(m => m.SceneRevision)
            .ThenByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }
}
