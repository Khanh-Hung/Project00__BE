using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.Scene;

public sealed class CharacterVisualProfileReader : ICharacterVisualProfileReader
{
    private readonly ProjectDbContext _dbContext;

    public CharacterVisualProfileReader(ProjectDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CharacterVisualProfile?> GetProfileByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterVisualProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);
    }
}
