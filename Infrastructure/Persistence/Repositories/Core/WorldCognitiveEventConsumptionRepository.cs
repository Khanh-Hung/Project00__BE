using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Domain.Entities;
using Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories.Core;

public sealed class WorldCognitiveEventConsumptionRepository : IWorldCognitiveEventConsumptionRepository
{
    private readonly CoreDbContext _context;

    public WorldCognitiveEventConsumptionRepository(CoreDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<WorldCognitiveEventConsumption?> GetByEventIdAsync(Guid eventId, CancellationToken ct = default)
    {
        if (eventId == Guid.Empty) return null;

        return await _context.WorldCognitiveEventConsumptions
            .FirstOrDefaultAsync(c => c.EventId == eventId, ct);
    }

    public async Task<(bool IsClaimed, WorldCognitiveEventConsumption Consumption)> TryClaimAsync(
        WorldCognitiveEventConsumption consumption,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consumption);

        // 1. Validate incoming fingerprint against canonical computation before existing lookup
        consumption.ValidateCanonicalFingerprint();

        // 2. Initial lookup check
        var existing = await GetByEventIdAsync(consumption.EventId, ct);
        if (existing != null)
        {
            if (string.Equals(existing.Fingerprint, consumption.Fingerprint, StringComparison.Ordinal))
            {
                return (false, existing);
            }

            throw new WorldCognitiveEventIdempotencyConflictException(
                consumption.EventId,
                existing.Fingerprint,
                consumption.Fingerprint);
        }

        // 3. Attempt insert
        try
        {
            await _context.WorldCognitiveEventConsumptions.AddAsync(consumption, ct);
            await _context.SaveChangesAsync(ct);
            return (true, consumption);
        }
        catch (DbUpdateException)
        {
            // Concurrent race: detach candidate and reload authoritative row from database
            _context.Entry(consumption).State = EntityState.Detached;

            var concurrent = await _context.WorldCognitiveEventConsumptions
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.EventId == consumption.EventId, ct);

            if (concurrent != null)
            {
                if (string.Equals(concurrent.Fingerprint, consumption.Fingerprint, StringComparison.Ordinal))
                {
                    return (false, concurrent);
                }

                throw new WorldCognitiveEventIdempotencyConflictException(
                    consumption.EventId,
                    concurrent.Fingerprint,
                    consumption.Fingerprint);
            }

            throw;
        }
    }

    public async Task MarkConsumedAsync(Guid eventId, Guid cycleId, DateTime consumedAtUtc, CancellationToken ct = default)
    {
        var record = await _context.WorldCognitiveEventConsumptions
            .FirstOrDefaultAsync(c => c.EventId == eventId, ct);

        if (record != null)
        {
            record.MarkConsumed(cycleId, consumedAtUtc);
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task MarkFailedAsync(Guid eventId, string failureReason, CancellationToken ct = default)
    {
        var record = await _context.WorldCognitiveEventConsumptions
            .FirstOrDefaultAsync(c => c.EventId == eventId, ct);

        if (record != null)
        {
            record.MarkFailed(failureReason);
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
