using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public sealed class CharacterRelationshipRepository : GenericRepository<CharacterRelationship>, ICharacterRelationshipRepository
{
    public CharacterRelationshipRepository(CoreDbContext dbContext) : base(dbContext)
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

    public async Task<CharacterRelationship?> GetByTargetAsync(
        Guid characterId,
        RelationshipTargetType targetType,
        Guid targetId,
        CancellationToken ct = default)
    {
        var local = DbContext.ChangeTracker.Entries<CharacterRelationship>()
            .Select(e => e.Entity)
            .FirstOrDefault(r => r.CharacterId == characterId && r.TargetType == targetType && r.TargetId == targetId && !r.IsSoftDeleted);

        if (local != null)
        {
            return local;
        }

        return await DbContext.CharacterRelationships
            .FirstOrDefaultAsync(r => r.CharacterId == characterId && r.TargetType == targetType && r.TargetId == targetId && !r.IsSoftDeleted, ct);
    }

    public async Task<CharacterRelationship> GetOrCreateByTargetAsync(
        Guid characterId,
        RelationshipTargetType targetType,
        Guid targetId,
        CancellationToken ct = default)
    {
        var existing = await GetByTargetAsync(characterId, targetType, targetId, ct);
        if (existing != null)
        {
            return existing;
        }

        var newRelationship = CharacterRelationship.Create(
            characterId: characterId,
            targetType: targetType,
            targetId: targetId,
            relationshipType: RelationshipType.Stranger,
            trust: 0,
            affection: 0,
            familiarity: 0);

        try
        {
            await DbContext.CharacterRelationships.AddAsync(newRelationship, ct);
            await DbContext.SaveChangesAsync(ct);
            return newRelationship;
        }
        catch (DbUpdateException)
        {
            // Race-safe fallback: If another execution concurrently created the relationship,
            // query the authoritative record from DB.
            var concurrent = await DbContext.CharacterRelationships
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.CharacterId == characterId && r.TargetType == targetType && r.TargetId == targetId && !r.IsSoftDeleted, ct);

            if (concurrent != null)
            {
                return concurrent;
            }

            throw;
        }
    }
}
