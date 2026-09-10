using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.Scene;

public sealed class CharacterVisualProfileReader : ICharacterVisualProfileReader
{
    private readonly CoreDbContext _dbContext;

    public CharacterVisualProfileReader(CoreDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CharacterVisualProfile?> GetProfileByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterVisualProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);
    }

    public async Task<CharacterVisualIdentity?> GetVisualIdentityByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
    {
        var character = await _dbContext.Characters
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == characterId, ct);

        return character?.VisualIdentity;
    }
}
