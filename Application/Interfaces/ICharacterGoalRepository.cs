using Domain.Entities;

namespace Application.Interfaces;

public interface ICharacterGoalRepository
{
    Task<IReadOnlyList<CharacterGoal>> GetActiveGoalsByCharacterIdAsync(Guid characterId, CancellationToken ct = default);
    Task<CharacterGoal?> GetByIdAsync(Guid goalId, CancellationToken ct = default);
    Task AddAsync(CharacterGoal goal, CancellationToken ct = default);
    Task UpdateAsync(CharacterGoal goal, CancellationToken ct = default);
}
