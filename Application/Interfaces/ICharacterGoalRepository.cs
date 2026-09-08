using Domain.Entities;

namespace Application.Interfaces;

public interface ICharacterGoalRepository
{
    Task<CharacterGoal?> GetByIdAsync(Guid goalId, CancellationToken ct = default);
    Task<IReadOnlyList<CharacterGoal>> GetActiveGoalsAsync(Guid characterId, CancellationToken ct = default);
    Task<IReadOnlyList<CharacterGoal>> GetActiveGoalsByCharacterIdAsync(Guid characterId, CancellationToken ct = default);
    Task<IReadOnlyList<CharacterGoal>> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default);
    Task<CharacterGoal?> GetHighestPriorityActiveGoalAsync(Guid characterId, CancellationToken ct = default);
    Task<CharacterGoal?> GetActiveByGoalKeyAsync(Guid characterId, string goalKey, CancellationToken ct = default);
    Task AddAsync(CharacterGoal goal, CancellationToken ct = default);
    Task UpdateAsync(CharacterGoal goal, CancellationToken ct = default);
}
