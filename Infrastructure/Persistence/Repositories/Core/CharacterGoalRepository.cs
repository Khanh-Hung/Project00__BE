using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

/// <summary>
/// Authoritative persistence repository for CharacterGoal aggregates.
/// Strictly enforces character scoping, deterministic ordering, and optimistic concurrency.
/// </summary>
public sealed class CharacterGoalRepository : ICharacterGoalRepository
{
    private readonly CoreDbContext _dbContext;

    public CharacterGoalRepository(CoreDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<CharacterGoal?> GetByIdAsync(Guid goalId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterGoals
            .Include(g => g.Milestones)
            .FirstOrDefaultAsync(g => g.Id == goalId, ct);
    }

    public async Task<IReadOnlyList<CharacterGoal>> GetActiveGoalsAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterGoals
            .Include(g => g.Milestones)
            .Where(g => g.CharacterId == characterId && g.Status == CharacterGoalStatus.Active)
            .OrderByDescending(g => g.Priority == CharacterGoalPriority.Critical ? 3 :
                                    g.Priority == CharacterGoalPriority.High ? 2 :
                                    g.Priority == CharacterGoalPriority.Normal ? 1 : 0)
            .ThenBy(g => g.Progress)
            .ThenBy(g => g.CreatedAt)
            .ThenBy(g => g.Id)
            .ToListAsync(ct);
    }

    public Task<IReadOnlyList<CharacterGoal>> GetActiveGoalsByCharacterIdAsync(Guid characterId, CancellationToken ct = default) =>
        GetActiveGoalsAsync(characterId, ct);

    public async Task<IReadOnlyList<CharacterGoal>> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterGoals
            .Include(g => g.Milestones)
            .Where(g => g.CharacterId == characterId)
            .OrderByDescending(g => g.CreatedAt)
            .ThenBy(g => g.Id)
            .ToListAsync(ct);
    }

    public async Task<CharacterGoal?> GetHighestPriorityActiveGoalAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterGoals
            .Include(g => g.Milestones)
            .Where(g => g.CharacterId == characterId && g.Status == CharacterGoalStatus.Active)
            .OrderByDescending(g => g.Priority == CharacterGoalPriority.Critical ? 3 :
                                    g.Priority == CharacterGoalPriority.High ? 2 :
                                    g.Priority == CharacterGoalPriority.Normal ? 1 : 0)
            .ThenBy(g => g.Progress)
            .ThenBy(g => g.CreatedAt)
            .ThenBy(g => g.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<CharacterGoal?> GetActiveByGoalKeyAsync(Guid characterId, string goalKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalKey, nameof(goalKey));

        return await _dbContext.CharacterGoals
            .Include(g => g.Milestones)
            .FirstOrDefaultAsync(g => g.CharacterId == characterId &&
                                      g.Status == CharacterGoalStatus.Active &&
                                      g.Title == goalKey, ct);
    }

    public async Task AddAsync(CharacterGoal goal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(goal, nameof(goal));
        await _dbContext.CharacterGoals.AddAsync(goal, ct);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(CharacterGoal goal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(goal, nameof(goal));
        _dbContext.CharacterGoals.Update(goal);
        await _dbContext.SaveChangesAsync(ct);
    }
}
