using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public sealed class CharacterPersonalityRepository : ICharacterPersonalityRepository
{
    private readonly CoreDbContext _dbContext;

    public CharacterPersonalityRepository(CoreDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<CharacterPersonality?> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty) return null;

        return await _dbContext.CharacterPersonalities
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);
    }

    public async Task<CharacterPersonality> GetOrCreateDefaultAsync(Guid characterId, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        var existing = await _dbContext.CharacterPersonalities
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

        if (existing != null)
        {
            return existing;
        }

        var newPersonality = CharacterPersonality.CreateDefault(characterId);

        try
        {
            await _dbContext.CharacterPersonalities.AddAsync(newPersonality, ct);
            await _dbContext.SaveChangesAsync(ct);
            return newPersonality;
        }
        catch (DbUpdateException)
        {
            // Clean up locally created entity to keep ChangeTracker pristine
            _dbContext.Entry(newPersonality).State = EntityState.Detached;

            var winner = await _dbContext.CharacterPersonalities
                .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

            if (winner != null)
            {
                return winner;
            }

            throw;
        }
    }

    public async Task AddAsync(CharacterPersonality personality, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(personality);
        await _dbContext.CharacterPersonalities.AddAsync(personality, ct);
    }
}
