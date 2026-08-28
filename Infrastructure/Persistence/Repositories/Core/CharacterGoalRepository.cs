using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public sealed class CharacterGoalRepository : ICharacterGoalRepository
{
    private readonly CoreDbContext _dbContext;

    public CharacterGoalRepository(CoreDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<CharacterGoal>> GetActiveGoalsByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterGoals
            .Include(g => g.Milestones)
            .Where(g => g.CharacterId == characterId && g.Status == CharacterGoalStatus.Active)
            .OrderByDescending(g => g.Priority)
            .ThenByDescending(g => g.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<CharacterGoal?> GetByIdAsync(Guid goalId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterGoals
            .Include(g => g.Milestones)
            .FirstOrDefaultAsync(g => g.Id == goalId, ct);
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
