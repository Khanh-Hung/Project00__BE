using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.LifeSimulation;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories.Core;

public sealed class CharacterOutboxRepository : ICharacterOutboxRepository
{
    private readonly CoreDbContext _context;

    public CharacterOutboxRepository(CoreDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<CharacterOutboxMessage?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.CharacterOutboxMessages
            .FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<CharacterOutboxMessage?> GetByEventIdAsync(Guid eventId, CancellationToken ct = default)
    {
        if (eventId == Guid.Empty) return null;

        return await _context.CharacterOutboxMessages
            .FirstOrDefaultAsync(m => m.EventId == eventId, ct);
    }

    public async Task<CharacterOutboxMessage> AddOrGetAsync(CharacterOutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 1. Initial check
        var existing = await GetByEventIdAsync(message.EventId, ct);
        if (existing != null)
        {
            if (existing.Fingerprint == message.Fingerprint)
            {
                return existing;
            }

            throw new CharacterOutboxIdempotencyConflictException(
                message.EventId,
                existing.Fingerprint,
                message.Fingerprint);
        }

        try
        {
            await _context.CharacterOutboxMessages.AddAsync(message, ct);
            await _context.SaveChangesAsync(ct);
            return message;
        }
        catch (DbUpdateException)
        {
            // Concurrent race: detach candidate and reload authoritative row from database
            _context.Entry(message).State = EntityState.Detached;

            var concurrent = await _context.CharacterOutboxMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.EventId == message.EventId, ct);

            if (concurrent != null)
            {
                if (concurrent.Fingerprint == message.Fingerprint)
                {
                    return concurrent;
                }

                throw new CharacterOutboxIdempotencyConflictException(
                    message.EventId,
                    concurrent.Fingerprint,
                    message.Fingerprint);
            }

            throw;
        }
    }

    public async Task AddAsync(CharacterOutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _context.CharacterOutboxMessages.AddAsync(message, ct);
    }

    public async Task AddRangeAsync(IEnumerable<CharacterOutboxMessage> messages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await _context.CharacterOutboxMessages.AddRangeAsync(messages, ct);
    }

    public async Task<IReadOnlyList<CharacterOutboxMessage>> GetPendingMessagesAsync(
        Guid? characterId = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var query = _context.CharacterOutboxMessages
            .Where(m => m.Status == CharacterOutboxStatus.Pending);

        if (characterId.HasValue && characterId.Value != Guid.Empty)
        {
            query = query.Where(m => m.CharacterId == characterId.Value);
        }

        return await query
            .OrderBy(m => m.OccurredAtUtc)
            .ThenBy(m => m.CreatedAtUtc)
            .ThenBy(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
