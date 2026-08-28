using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.Scene;

public sealed class CanonicalReferenceReader : ICanonicalReferenceReader
{
    private readonly ProjectDbContext _dbContext;

    public CanonicalReferenceReader(ProjectDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CharacterVisualReference?> GetActiveCanonicalReferenceAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterVisualReferences
            .AsNoTracking()
            .Where(r => r.CharacterId == characterId && r.IsCanonical && r.Status == VisualReferenceStatus.Active)
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.PromotedAt)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }
}
