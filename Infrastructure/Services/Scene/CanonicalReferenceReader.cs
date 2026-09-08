using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.Scene;

public sealed class CanonicalReferenceReader : ICanonicalReferenceReader
{
    private readonly CoreDbContext _dbContext;

    public CanonicalReferenceReader(CoreDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CharacterVisualReference?> GetActiveCanonicalReferenceAsync(Guid characterId, CancellationToken ct = default)
    {
        var existing = await _dbContext.CharacterVisualReferences
            .AsNoTracking()
            .Where(r => r.CharacterId == characterId && r.IsCanonical && r.Status == VisualReferenceStatus.Active)
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.PromotedAt)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
            return existing;

        var character = await _dbContext.Characters
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == characterId, ct);

        if (character != null)
        {
            var fallbackUrl = character.VisualIdentity?.CanonicalReferenceUrl ?? character.AvatarUrl;
            if (!string.IsNullOrWhiteSpace(fallbackUrl))
            {
                return new CharacterVisualReference(
                    characterId,
                    fallbackUrl,
                    type: VisualReferenceType.Canonical,
                    status: VisualReferenceStatus.Active,
                    isCanonical: true);
            }
        }

        return null;
    }
}
