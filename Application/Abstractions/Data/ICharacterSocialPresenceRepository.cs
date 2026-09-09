using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;

namespace Application.Abstractions.Data;

public interface ICharacterSocialPresenceRepository
{
    Task<CharacterSocialPresence?> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default);
    Task<(bool IsCreated, CharacterSocialPresence Presence)> TryCreateAsync(CharacterSocialPresence candidate, CancellationToken ct = default);
    Task<CharacterSocialPresence> UpdateAsync(CharacterSocialPresence presence, CancellationToken ct = default);
    Task<CharacterSocialPresenceTransition?> GetTransitionAsync(Guid characterId, Guid executionId, CancellationToken ct = default);
    Task<CharacterSocialPresenceTransition> AddTransitionAsync(CharacterSocialPresenceTransition transition, CancellationToken ct = default);
    Task<IReadOnlyList<CharacterSocialPresenceTransition>> GetRecentTransitionsAsync(Guid characterId, int limit = 10, CancellationToken ct = default);
}
