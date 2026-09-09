using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories.Core;

public sealed class CharacterAutonomousLifeTickRepository : ICharacterAutonomousLifeTickRepository
{
    private readonly CoreDbContext _context;

    public CharacterAutonomousLifeTickRepository(CoreDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<CharacterAutonomousLifeTick?> GetByTickIdAsync(
        Guid characterId,
        Guid simulationTickId,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty || simulationTickId == Guid.Empty) return null;

        return await _context.CharacterAutonomousLifeTicks
            .FirstOrDefaultAsync(t => t.CharacterId == characterId && t.SimulationTickId == simulationTickId, ct);
    }

    public async Task<(bool IsClaimed, CharacterAutonomousLifeTick Tick)> TryClaimAsync(
        CharacterAutonomousLifeTick candidate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // 1. Initial lookup
        var existing = await GetByTickIdAsync(candidate.CharacterId, candidate.SimulationTickId, ct);
        if (existing != null)
        {
            existing.ValidatePayload(candidate.SimulationTimeUtc);
            return (false, existing);
        }

        // 2. Attempt insert
        try
        {
            await _context.CharacterAutonomousLifeTicks.AddAsync(candidate, ct);
            await _context.SaveChangesAsync(ct);
            return (true, candidate);
        }
        catch (DbUpdateException)
        {
            // Concurrent race on insert: detach candidate and reload authoritative row from database
            _context.Entry(candidate).State = EntityState.Detached;

            var concurrent = await GetByTickIdAsync(candidate.CharacterId, candidate.SimulationTickId, ct);
            if (concurrent != null)
            {
                concurrent.ValidatePayload(candidate.SimulationTimeUtc);
                return (false, concurrent);
            }

            throw;
        }
    }

    public async Task UpdateAsync(
        CharacterAutonomousLifeTick tick,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tick);

        _context.CharacterAutonomousLifeTicks.Update(tick);
        await _context.SaveChangesAsync(ct);
    }
}
