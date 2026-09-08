using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Domain.Entities;
using Domain.Enums;
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
        TimeSpan? leaseTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consumption);

        // 1. Validate incoming fingerprint against canonical computation before existing lookup
        consumption.ValidateCanonicalFingerprint();

        var timeout = leaseTimeout ?? TimeSpan.FromMinutes(5);

        // 2. Initial lookup check
        var existing = await GetByEventIdAsync(consumption.EventId, ct);
        if (existing != null)
        {
            return await HandleExistingClaimAsync(existing, consumption, timeout, ct);
        }

        // 3. Attempt insert for new event
        try
        {
            await _context.WorldCognitiveEventConsumptions.AddAsync(consumption, ct);
            await _context.SaveChangesAsync(ct);
            return (true, consumption);
        }
        catch (DbUpdateException)
        {
            // Concurrent race on insert: detach candidate and reload authoritative row from database
            _context.Entry(consumption).State = EntityState.Detached;

            var concurrent = await _context.WorldCognitiveEventConsumptions
                .FirstOrDefaultAsync(c => c.EventId == consumption.EventId, ct);

            if (concurrent != null)
            {
                return await HandleExistingClaimAsync(concurrent, consumption, timeout, ct);
            }

            throw;
        }
    }

    public async Task<(bool IsReclaimed, WorldCognitiveEventConsumption Consumption)> ReclaimAsync(
        Guid eventId,
        DateTime attemptedAtUtc,
        CancellationToken ct = default)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("EventId cannot be empty.", nameof(eventId));

        var existing = await GetByEventIdAsync(eventId, ct);
        if (existing == null)
            throw new InvalidOperationException($"Cannot reclaim non-existent WorldCognitiveEvent consumption for EventId '{eventId:D}'.");

        if (existing.State == EventConsumptionState.Consumed)
            throw new InvalidOperationException($"Cannot reclaim an already Consumed event for EventId '{eventId:D}'.");

        try
        {
            existing.Reclaim(attemptedAtUtc);
            await _context.SaveChangesAsync(ct);
            return (true, existing);
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.Entry(existing).State = EntityState.Detached;
            var reloaded = await _context.WorldCognitiveEventConsumptions
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.EventId == eventId, ct);

            return (false, reloaded ?? existing);
        }
    }

    private async Task<(bool IsClaimed, WorldCognitiveEventConsumption Consumption)> HandleExistingClaimAsync(
        WorldCognitiveEventConsumption existing,
        WorldCognitiveEventConsumption incoming,
        TimeSpan leaseTimeout,
        CancellationToken ct)
    {
        // Strict divergence detection: same EventId but different fingerprint is a conflict across all states
        if (!string.Equals(existing.Fingerprint, incoming.Fingerprint, StringComparison.Ordinal))
        {
            throw new WorldCognitiveEventIdempotencyConflictException(
                incoming.EventId,
                existing.Fingerprint,
                incoming.Fingerprint);
        }

        // Terminal state: Consumed is permanent duplicate
        if (existing.State == EventConsumptionState.Consumed)
        {
            return (false, existing);
        }

        var now = incoming.LastAttemptAtUtc;

        // In-flight active lease check for InProgress state
        if (existing.State == EventConsumptionState.InProgress)
        {
            var elapsed = now - existing.LastAttemptAtUtc;
            if (elapsed >= TimeSpan.Zero && elapsed < leaseTimeout)
            {
                // Another worker is actively processing within the lease window
                return (false, existing);
            }

            // Elapsed >= leaseTimeout: worker crashed or timed out -> fall through to reclaim
        }

        // State is either Failed (retryable) or InProgress with expired lease (crash recovery)
        try
        {
            existing.Reclaim(now);
            await _context.SaveChangesAsync(ct);
            return (true, existing);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another concurrent worker claimed the recovery/retry slot
            _context.Entry(existing).State = EntityState.Detached;
            var reloaded = await _context.WorldCognitiveEventConsumptions
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.EventId == incoming.EventId, ct);

            return (false, reloaded ?? existing);
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

    public async Task MarkFailedAsync(Guid eventId, string failureReason, DateTime failedAtUtc, CancellationToken ct = default)
    {
        var record = await _context.WorldCognitiveEventConsumptions
            .FirstOrDefaultAsync(c => c.EventId == eventId, ct);

        if (record != null)
        {
            record.MarkFailed(failureReason, failedAtUtc);
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
