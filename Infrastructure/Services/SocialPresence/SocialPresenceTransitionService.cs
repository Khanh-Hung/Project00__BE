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
        var expectedFingerprint = CharacterSocialPresenceTransition.ComputeFingerprint(
            characterId, executionId, actionTypeStr, targetType, targetId);

        // 1. Check existing transition for idempotency
        var existingTransition = await _repository.GetTransitionAsync(characterId, executionId, ct);
        if (existingTransition != null)
        {
            if (existingTransition.OperationFingerprint != expectedFingerprint)
            {
                throw new InvalidOperationException(
                    $"Divergent semantic replay for ExecutionId '{executionId:D}' on CharacterId '{characterId:D}'. Existing fingerprint: {existingTransition.OperationFingerprint}, Incoming: {expectedFingerprint}");
            }

            var currentPresence = await GetOrCreateAsync(characterId, now, ct);
            return new CharacterSocialPresenceFeedback(
                PresenceId: currentPresence.Id,
                CharacterId: characterId,
                ExecutionId: executionId,
                Status: existingTransition.NewStatus,
                CurrentActivityType: existingTransition.NewActivityType,
                TargetType: existingTransition.TargetType,
                TargetId: existingTransition.TargetId,
                UpdatedAtUtc: new DateTimeOffset(existingTransition.AppliedAtUtc, TimeSpan.Zero),
                IsSuccess: true
            );
        }

        // 2. Map ActionType to LifeActivityType
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

        var presence = await GetOrCreateAsync(characterId, now, ct);
        var oldStatus = presence.Status;
        var oldActivity = presence.CurrentActivityType;
        var versionBefore = presence.Version;

        // If offline, reactivate to Active when an action executes
        if (presence.Status == SocialPresenceStatus.Offline)
        {
            presence.Activate(now);
        }

        presence.UpdateActivity(targetActivity, now, targetType, targetId);
        await _repository.UpdateAsync(presence, ct);

        var transition = new CharacterSocialPresenceTransition(
            characterId: characterId,
            executionId: executionId,
            actionType: actionTypeStr,
            oldStatus: oldStatus,
            newStatus: presence.Status,
            oldActivityType: oldActivity,
            newActivityType: presence.CurrentActivityType,
            targetType: targetType,
            targetId: targetId,
            versionBefore: versionBefore,
            versionAfter: presence.Version,
            appliedAtUtc: now
        );

        try
        {
            await _repository.AddTransitionAsync(transition, ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent race on transition insert: reload existing
            var concurrent = await _repository.GetTransitionAsync(characterId, executionId, ct);
            if (concurrent != null)
            {
                if (concurrent.OperationFingerprint != expectedFingerprint)
                {
                    throw new InvalidOperationException(
                        $"Divergent semantic replay for ExecutionId '{executionId:D}' on CharacterId '{characterId:D}'. Existing fingerprint: {concurrent.OperationFingerprint}, Incoming: {expectedFingerprint}");
                }

                return new CharacterSocialPresenceFeedback(
                    PresenceId: presence.Id,
                    CharacterId: characterId,
                    ExecutionId: executionId,
                    Status: concurrent.NewStatus,
                    CurrentActivityType: concurrent.NewActivityType,
                    TargetType: concurrent.TargetType,
                    TargetId: concurrent.TargetId,
                    UpdatedAtUtc: new DateTimeOffset(concurrent.AppliedAtUtc, TimeSpan.Zero),
                    IsSuccess: true
                );
            }

            throw;
        }

        return new CharacterSocialPresenceFeedback(
            PresenceId: presence.Id,
            CharacterId: characterId,
            ExecutionId: executionId,
            Status: presence.Status,
            CurrentActivityType: presence.CurrentActivityType,
            TargetType: targetType,
            TargetId: targetId,
            UpdatedAtUtc: now,
            IsSuccess: true
        );
    }
}
