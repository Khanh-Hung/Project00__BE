using Application.Interfaces;
using Domain.Entities;
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
        var profile = await _dbContext.CharacterVisualProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

        if (profile != null)
            return profile;

        var character = await _dbContext.Characters
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == characterId, ct);

        if (character?.VisualIdentity != null)
        {
            return new CharacterVisualProfile(
                characterId: characterId,
                eyeColor: character.VisualIdentity.Eyes,
                hairColor: character.VisualIdentity.Hair,
                skinTone: character.VisualIdentity.Skin,
                facialFeatures: character.VisualIdentity.Face,
                bodyIdentity: character.VisualIdentity.Body,
                currentOutfit: character.VisualIdentity.ClothingStyle
            );
        }

        return null;
    }
}
