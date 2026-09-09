using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories.Core;

public sealed class CharacterSocialPresenceRepository : ICharacterSocialPresenceRepository
{
    private readonly CoreDbContext _context;

    public CharacterSocialPresenceRepository(CoreDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<CharacterSocialPresence?> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty) return null;

        return await _context.CharacterSocialPresences
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);
    }

    public async Task<(bool IsCreated, CharacterSocialPresence Presence)> TryCreateAsync(
        CharacterSocialPresence candidate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // 1. Initial lookup
        var existing = await GetByCharacterIdAsync(candidate.CharacterId, ct);
        if (existing != null)
        {
            return (false, existing);
        }

        // 2. Attempt insert
        try
        {
            await _context.CharacterSocialPresences.AddAsync(candidate, ct);
            await _context.SaveChangesAsync(ct);
            return (true, candidate);
        }
        catch (DbUpdateException)
        {
            // Race condition on insert: detach candidate and reload authoritative row from DB
            _context.Entry(candidate).State = EntityState.Detached;

            var concurrent = await GetByCharacterIdAsync(candidate.CharacterId, ct);
            if (concurrent != null)
            {
                return (false, concurrent);
            }

            throw;
        }
    }

    public async Task<CharacterSocialPresence> UpdateAsync(CharacterSocialPresence presence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(presence);

        _context.CharacterSocialPresences.Update(presence);
        await _context.SaveChangesAsync(ct);
        return presence;
    }

    public async Task<CharacterSocialPresenceTransition?> GetTransitionAsync(Guid characterId, Guid executionId, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty || executionId == Guid.Empty) return null;

        return await _context.CharacterSocialPresenceTransitions
            .FirstOrDefaultAsync(t => t.CharacterId == characterId && t.ExecutionId == executionId, ct);
    }

    public async Task<CharacterSocialPresenceTransition> AddTransitionAsync(CharacterSocialPresenceTransition transition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transition);

        await _context.CharacterSocialPresenceTransitions.AddAsync(transition, ct);
        await _context.SaveChangesAsync(ct);
        return transition;
    }

    public async Task<IReadOnlyList<CharacterSocialPresenceTransition>> GetRecentTransitionsAsync(
        Guid characterId, int limit = 10, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty) return Array.Empty<CharacterSocialPresenceTransition>();

        return await _context.CharacterSocialPresenceTransitions
            .Where(t => t.CharacterId == characterId)
            .OrderByDescending(t => t.AppliedAtUtc)
            .Take(limit)
            .ToListAsync(ct);
    }
}
