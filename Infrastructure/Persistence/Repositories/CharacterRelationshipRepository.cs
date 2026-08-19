using Application.Abstractions.Data;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public sealed class CharacterRelationshipRepository : GenericRepository<CharacterRelationship>, ICharacterRelationshipRepository
{
    public CharacterRelationshipRepository(ProjectDbContext dbContext) : base(dbContext)
    {
    }

    public async Task<CharacterRelationship?> GetByPairAsync(Guid userId, Guid characterId, CancellationToken ct = default)
    {
        // 1. Check local change tracker first to avoid duplicate tracking in the same unit of work
        var local = DbContext.ChangeTracker.Entries<CharacterRelationship>()
            .Select(e => e.Entity)
            .FirstOrDefault(r => r.UserId == userId && r.CharacterId == characterId && !r.IsSoftDeleted);

        if (local != null)
        {
            return local;
        }

        // 2. Query from database
        return await DbContext.CharacterRelationships
            .FirstOrDefaultAsync(r => r.UserId == userId && r.CharacterId == characterId && !r.IsSoftDeleted, ct);
    }

    public async Task<CharacterRelationship> GetOrCreateAsync(
        Guid userId,
        Guid characterId,
        int initialAffection = 0,
        CharacterMood initialMood = CharacterMood.Neutral,
        CancellationToken ct = default)
    {
        var existing = await GetByPairAsync(userId, characterId, ct);
        if (existing != null)
        {
            return existing;
        }

        var newRelationship = CharacterRelationship.Create(
            characterId,
            userId,
            initialAffection,
            initialMood);

        await DbContext.CharacterRelationships.AddAsync(newRelationship, ct);
        return newRelationship;
    }
}
