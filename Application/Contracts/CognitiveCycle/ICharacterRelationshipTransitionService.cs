using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Explicit mutation boundary service for applying relationship deltas, enforcing idempotency replay,
/// handling optimistic concurrency, and persisting the transition ledger entry.
/// </summary>
public interface ICharacterRelationshipTransitionService
{
    Task<CharacterRelationshipFeedback?> ApplyTransitionAsync(
        Guid characterId,
        Guid executionId,
        Guid targetId,
        RelationshipTargetType targetType,
        int trustDelta,
        int affectionDelta,
        int familiarityDelta,
        RelationshipType? newRelationshipType,
        string? reason,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct = default);
}
