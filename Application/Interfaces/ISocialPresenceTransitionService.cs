using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Authoritative mutation service for CharacterSocialPresence aggregate.
/// Encapsulates status transitions, activity changes, and action execution feedback with idempotency checks.
/// </summary>
public interface ISocialPresenceTransitionService
{
    Task<CharacterSocialPresence> GetOrCreateAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default);
    Task<CharacterSocialPresence> ActivateAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default);
    Task<CharacterSocialPresence> SetAwayAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default);
    Task<CharacterSocialPresence> SetOfflineAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default);
    Task<CharacterSocialPresence> SetActivityAsync(Guid characterId, LifeActivityType activityType, DateTimeOffset now, RelationshipTargetType? targetType = null, Guid? targetId = null, CancellationToken ct = default);
    Task<CharacterSocialPresence> SetVisibilityAsync(Guid characterId, SocialPresenceVisibility visibility, DateTimeOffset now, CancellationToken ct = default);
    Task<CharacterSocialPresenceFeedback?> ApplyActionExecutionFeedbackAsync(
        Guid characterId,
        Guid executionId,
        CharacterActionExecutionResult actionExecution,
        DateTimeOffset now,
        RelationshipTargetType? targetType = null,
        Guid? targetId = null,
        CancellationToken ct = default);
}
