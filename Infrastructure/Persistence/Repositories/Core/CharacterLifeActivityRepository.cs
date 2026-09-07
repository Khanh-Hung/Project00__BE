using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories.Core;

public sealed class CharacterLifeActivityRepository : ICharacterLifeActivityRepository
{
    private readonly CoreDbContext _context;

    public CharacterLifeActivityRepository(CoreDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<CharacterLifeActivity?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.CharacterLifeActivities
            .FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task<CharacterLifeActivity?> GetActiveActivityAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _context.CharacterLifeActivities
            .Where(a => a.CharacterId == characterId && a.Status == LifeActivityStatus.Active)
            .OrderBy(a => a.StartAtUtc)
            .ThenBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<CharacterLifeActivity>> GetActiveActivitiesAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _context.CharacterLifeActivities
            .Where(a => a.CharacterId == characterId && a.Status == LifeActivityStatus.Active)
            .OrderBy(a => a.StartAtUtc)
            .ThenBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);
    }

    public async Task<CharacterLifeActivity?> GetNextScheduledActivityAsync(Guid characterId, DateTime asOfUtc, CancellationToken ct = default)
    {
        return await _context.CharacterLifeActivities
            .Where(a => a.CharacterId == characterId && a.Status == LifeActivityStatus.Scheduled && a.StartAtUtc <= asOfUtc)
            .OrderBy(a => a.StartAtUtc)
            .ThenBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<CharacterLifeActivity>> GetScheduledActivitiesAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _context.CharacterLifeActivities
            .Where(a => a.CharacterId == characterId && a.Status == LifeActivityStatus.Scheduled)
            .OrderBy(a => a.StartAtUtc)
            .ThenBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CharacterLifeActivity>> GetActivitiesInTimeRangeAsync(Guid characterId, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        return await _context.CharacterLifeActivities
            .Where(a => a.CharacterId == characterId &&
                        (a.Status == LifeActivityStatus.Active || a.Status == LifeActivityStatus.Scheduled) &&
                        a.StartAtUtc < endUtc &&
                        a.PlannedEndAtUtc > startUtc)
            .OrderBy(a => a.StartAtUtc)
            .ThenBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);
    }

    public async Task<bool> HasOverlappingActivityAsync(
        Guid characterId,
        DateTime startAtUtc,
        DateTime plannedEndAtUtc,
        Guid? excludeActivityId = null,
        CancellationToken ct = default)
    {
        return await _context.CharacterLifeActivities
            .AnyAsync(a => a.CharacterId == characterId &&
                           (a.Status == LifeActivityStatus.Active || a.Status == LifeActivityStatus.Scheduled) &&
                           (!excludeActivityId.HasValue || a.Id != excludeActivityId.Value) &&
                           a.StartAtUtc < plannedEndAtUtc &&
                           a.PlannedEndAtUtc > startAtUtc, ct);
    }

    public async Task<CharacterLifeActivity?> GetOverlappingActivityAsync(
        Guid characterId,
        DateTime startAtUtc,
        DateTime plannedEndAtUtc,
        Guid? excludeActivityId = null,
        CancellationToken ct = default)
    {
        return await _context.CharacterLifeActivities
            .Where(a => a.CharacterId == characterId &&
                        (a.Status == LifeActivityStatus.Active || a.Status == LifeActivityStatus.Scheduled) &&
                        (!excludeActivityId.HasValue || a.Id != excludeActivityId.Value) &&
                        a.StartAtUtc < plannedEndAtUtc &&
                        a.PlannedEndAtUtc > startAtUtc)
            .OrderBy(a => a.StartAtUtc)
            .ThenBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task AddAsync(CharacterLifeActivity activity, CancellationToken ct = default)
    {
        await _context.CharacterLifeActivities.AddAsync(activity, ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
