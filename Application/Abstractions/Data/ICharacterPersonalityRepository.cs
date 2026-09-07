using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;

namespace Application.Abstractions.Data;

public interface ICharacterPersonalityRepository
{
    Task<CharacterPersonality?> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default);
    Task<CharacterPersonality> GetOrCreateDefaultAsync(Guid characterId, CancellationToken ct = default);
    Task AddAsync(CharacterPersonality personality, CancellationToken ct = default);
}
