using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories.Core;

public sealed class CharacterSocialPresenceRepository : ICharacterSocialPresenceRepository
{
    private readonly CoreDbContext _context;

    public CharacterSocialPresenceRepository(CoreDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<CharacterSocialPresence?> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty) return null;

        return await _context.CharacterSocialPresences
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);
    }

    public async Task<(bool IsCreated, CharacterSocialPresence Presence)> TryCreateAsync(
        CharacterSocialPresence candidate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // 1. Initial lookup
        var existing = await GetByCharacterIdAsync(candidate.CharacterId, ct);
        if (existing != null)
        {
            return (false, existing);
        }

        // 2. Attempt insert
        try
        {
            await _context.CharacterSocialPresences.AddAsync(candidate, ct);
            await _context.SaveChangesAsync(ct);
            return (true, candidate);
        }
        catch (DbUpdateException)
        {
            // Race condition on insert: detach candidate and reload authoritative row from DB
            _context.Entry(candidate).State = EntityState.Detached;

            var concurrent = await GetByCharacterIdAsync(candidate.CharacterId, ct);
            if (concurrent != null)
            {
                return (false, concurrent);
            }

            throw;
        }
    }

    public async Task<CharacterSocialPresence> UpdateAsync(CharacterSocialPresence presence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(presence);

        _context.CharacterSocialPresences.Update(presence);
        await _context.SaveChangesAsync(ct);
        return presence;
    }

    public async Task<CharacterSocialPresenceTransition?> GetTransitionAsync(Guid characterId, Guid executionId, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty || executionId == Guid.Empty) return null;

        return await _context.CharacterSocialPresenceTransitions
            .FirstOrDefaultAsync(t => t.CharacterId == characterId && t.ExecutionId == executionId, ct);
    }

    public async Task<CharacterSocialPresenceTransition> AddTransitionAsync(CharacterSocialPresenceTransition transition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transition);

        await _context.CharacterSocialPresenceTransitions.AddAsync(transition, ct);
        await _context.SaveChangesAsync(ct);
        return transition;
    }

    public async Task<IReadOnlyList<CharacterSocialPresenceTransition>> GetRecentTransitionsAsync(
        Guid characterId, int limit = 10, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty) return Array.Empty<CharacterSocialPresenceTransition>();

        return await _context.CharacterSocialPresenceTransitions
            .Where(t => t.CharacterId == characterId)
            .OrderByDescending(t => t.AppliedAtUtc)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<(CharacterSocialPresence Presence, CharacterSocialPresenceTransition Transition, bool IsDuplicate)> RecordTransitionAtomicAsync(
        Guid characterId,
        Guid executionId,
        string actionType,
        LifeActivityType targetActivity,
        RelationshipTargetType? targetType,
        Guid? targetId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (executionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));

        if (string.IsNullOrWhiteSpace(actionType))
            throw new ArgumentException("ActionType cannot be null or whitespace.", nameof(actionType));

        var expectedFingerprint = CharacterSocialPresenceTransition.ComputeFingerprint(
            characterId, executionId, actionType, targetActivity, targetType, targetId);

        // 1. Fast-path idempotency check
        var existingTransition = await _context.CharacterSocialPresenceTransitions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.CharacterId == characterId && t.ExecutionId == executionId, ct);

        if (existingTransition != null)
        {
            if (existingTransition.OperationFingerprint != expectedFingerprint)
            {
                throw new InvalidOperationException(
                    $"Divergent semantic replay for ExecutionId '{executionId:D}' on CharacterId '{characterId:D}'. Existing fingerprint: {existingTransition.OperationFingerprint}, Incoming: {expectedFingerprint}");
            }

            var currentPresence = await _context.CharacterSocialPresences
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

            if (currentPresence == null)
            {
                throw new InvalidOperationException(
                    $"Authoritative CharacterSocialPresence for CharacterId '{characterId:D}' not found for existing transition.");
            }

            return (currentPresence, existingTransition, true);
        }

        // 2. Atomic persistence boundary
        try
        {
            var presence = await _context.CharacterSocialPresences
                .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

            if (presence == null)
            {
                presence = CharacterSocialPresence.CreateDefault(characterId, now);
                await _context.CharacterSocialPresences.AddAsync(presence, ct);
            }

            var oldStatus = presence.Status;
            var oldActivity = presence.CurrentActivityType;
            var versionBefore = presence.Version;

            if (presence.Status == SocialPresenceStatus.Offline)
            {
                presence.Activate(now);
            }

            presence.UpdateActivity(targetActivity, now, targetType, targetId);

            var transition = new CharacterSocialPresenceTransition(
                characterId: characterId,
                executionId: executionId,
                actionType: actionType,
                oldStatus: oldStatus,
                newStatus: presence.Status,
                oldActivityType: oldActivity,
                newActivityType: presence.CurrentActivityType,
                versionBefore: versionBefore,
                versionAfter: presence.Version,
                appliedAtUtc: now,
                targetType: targetType,
                targetId: targetId
            );

            await _context.CharacterSocialPresenceTransitions.AddAsync(transition, ct);

            await _context.SaveChangesAsync(ct);

            return (presence, transition, false);
        }
        catch (DbUpdateException ex)
        {
            _context.ChangeTracker.Clear();

            var concurrent = await _context.CharacterSocialPresenceTransitions
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.CharacterId == characterId && t.ExecutionId == executionId, ct);

            if (concurrent != null)
            {
                if (concurrent.OperationFingerprint != expectedFingerprint)
                {
                    throw new InvalidOperationException(
                        $"Divergent semantic replay for ExecutionId '{executionId:D}' on CharacterId '{characterId:D}'. Existing fingerprint: {concurrent.OperationFingerprint}, Incoming: {expectedFingerprint}", ex);
                }

                var concurrentPresence = await _context.CharacterSocialPresences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

                if (concurrentPresence != null)
                {
                    return (concurrentPresence, concurrent, true);
                }
            }

            throw;
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }
    }
}
