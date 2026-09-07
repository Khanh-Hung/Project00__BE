using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;

namespace Application.Abstractions.Data;

/// <summary>
/// Abstraction for persisting, retrieving, and deduplicating character outbox messages.
/// Enforces character isolation, deterministic retrieval, and DB-level idempotency protection.
/// </summary>
public interface ICharacterOutboxRepository
{
    Task<CharacterOutboxMessage?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CharacterOutboxMessage?> GetByEventIdAsync(Guid eventId, CancellationToken ct = default);
    Task<CharacterOutboxMessage> AddOrGetAsync(CharacterOutboxMessage message, CancellationToken ct = default);
    Task AddAsync(CharacterOutboxMessage message, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<CharacterOutboxMessage> messages, CancellationToken ct = default);
    Task<IReadOnlyList<CharacterOutboxMessage>> GetPendingMessagesAsync(Guid? characterId = null, int limit = 50, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
