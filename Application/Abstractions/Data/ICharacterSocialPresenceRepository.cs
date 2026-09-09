using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;

namespace Application.Abstractions.Data;

public interface ICharacterSocialPresenceRepository
{
    Task<CharacterSocialPresence?> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default);
    Task<(bool IsCreated, CharacterSocialPresence Presence)> TryCreateAsync(CharacterSocialPresence candidate, CancellationToken ct = default);
    Task<CharacterSocialPresence> UpdateAsync(CharacterSocialPresence presence, CancellationToken ct = default);
    Task<CharacterSocialPresenceTransition?> GetTransitionAsync(Guid characterId, Guid executionId, CancellationToken ct = default);
    Task<CharacterSocialPresenceTransition> AddTransitionAsync(CharacterSocialPresenceTransition transition, CancellationToken ct = default);
    Task<IReadOnlyList<CharacterSocialPresenceTransition>> GetRecentTransitionsAsync(Guid characterId, int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Atomically records a social presence mutation and its audit transition ledger entry inside a single database transaction.
    /// Invariant: Presence mutation and Transition insert commit or roll back together.
    /// Invariant: Under concurrent execution races on the same (CharacterId, ExecutionId), winner commits and loser
    /// catches the conflict, reloads the winner's authoritative transition and presence, and returns with IsDuplicate = true.
    /// </summary>
    Task<(CharacterSocialPresence Presence, CharacterSocialPresenceTransition Transition, bool IsDuplicate)> RecordTransitionAtomicAsync(
        Guid characterId,
        Guid executionId,
        string actionType,
        LifeActivityType targetActivity,
        RelationshipTargetType? targetType,
        Guid? targetId,
        DateTimeOffset now,
        CancellationToken ct = default);
}
