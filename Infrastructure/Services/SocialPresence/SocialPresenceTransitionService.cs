using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.ActionExecution;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.SocialPresence;

public sealed class SocialPresenceTransitionService : ISocialPresenceTransitionService
{
    private readonly ICharacterSocialPresenceRepository _repository;
    private readonly ILogger<SocialPresenceTransitionService> _logger;

    public SocialPresenceTransitionService(
        ICharacterSocialPresenceRepository repository,
        ILogger<SocialPresenceTransitionService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterSocialPresence> GetOrCreateAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        var existing = await _repository.GetByCharacterIdAsync(characterId, ct);
        if (existing != null)
            return existing;

        var defaultPresence = CharacterSocialPresence.CreateDefault(characterId, now);
        var (isCreated, authoritative) = await _repository.TryCreateAsync(defaultPresence, ct);
        return authoritative;
    }

    public async Task<CharacterSocialPresence> ActivateAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default)
    {
        var presence = await GetOrCreateAsync(characterId, now, ct);
        presence.Activate(now);
        return await _repository.UpdateAsync(presence, ct);
    }

    public async Task<CharacterSocialPresence> SetAwayAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default)
    {
        var presence = await GetOrCreateAsync(characterId, now, ct);
        presence.SetAway(now);
        return await _repository.UpdateAsync(presence, ct);
    }

    public async Task<CharacterSocialPresence> SetOfflineAsync(Guid characterId, DateTimeOffset now, CancellationToken ct = default)
    {
        var presence = await GetOrCreateAsync(characterId, now, ct);
        presence.SetOffline(now);
        return await _repository.UpdateAsync(presence, ct);
    }

    public async Task<CharacterSocialPresence> SetActivityAsync(
        Guid characterId,
        LifeActivityType activityType,
        DateTimeOffset now,
        RelationshipTargetType? targetType = null,
        Guid? targetId = null,
        CancellationToken ct = default)
    {
        var presence = await GetOrCreateAsync(characterId, now, ct);
        presence.UpdateActivity(activityType, now, targetType, targetId);
        return await _repository.UpdateAsync(presence, ct);
    }

    public async Task<CharacterSocialPresence> SetVisibilityAsync(
        Guid characterId,
        SocialPresenceVisibility visibility,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var presence = await GetOrCreateAsync(characterId, now, ct);
        presence.UpdateVisibility(visibility, now);
        return await _repository.UpdateAsync(presence, ct);
    }

    public async Task<CharacterSocialPresenceFeedback?> ApplyActionExecutionFeedbackAsync(
        Guid characterId,
        Guid executionId,
        CharacterActionExecutionResult actionExecution,
        DateTimeOffset now,
        RelationshipTargetType? targetType = null,
        Guid? targetId = null,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (executionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));

        ArgumentNullException.ThrowIfNull(actionExecution);

        // Only applied or already executed actions produce social presence feedback
        if (actionExecution.Status != CharacterActionExecutionStatus.Applied &&
            actionExecution.Status != CharacterActionExecutionStatus.AlreadyExecuted)
        {
            return null;
        }

        var actionTypeStr = actionExecution.ActionType?.ToString() ?? "Unknown";

        // Map ActionType to LifeActivityType
        var targetActivity = actionExecution.ActionType switch
        {
            ActionType.Socialize => LifeActivityType.Socialize,
            ActionType.Eat => LifeActivityType.Eat,
            ActionType.Rest => LifeActivityType.Rest,
            ActionType.ReduceStress => LifeActivityType.PersonalActivity,
            ActionType.SeekComfort => LifeActivityType.Rest,
            ActionType.SeekSafety => LifeActivityType.Idle,
            _ => LifeActivityType.Idle
        };

        var (presence, transition, isDuplicate) = await _repository.RecordTransitionAtomicAsync(
            characterId,
            executionId,
            actionTypeStr,
            targetActivity,
            targetType,
            targetId,
            now,
            ct);

        return new CharacterSocialPresenceFeedback(
            PresenceId: presence.Id,
            CharacterId: characterId,
            ExecutionId: executionId,
            Status: transition.NewStatus,
            CurrentActivityType: transition.NewActivityType,
            TargetType: transition.TargetType,
            TargetId: transition.TargetId,
            UpdatedAtUtc: new DateTimeOffset(transition.AppliedAtUtc, TimeSpan.Zero),
            IsSuccess: true
        );
    }
}
