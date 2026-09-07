using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.CognitiveCycle;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.CognitiveCycle;

/// <summary>
/// Infrastructure implementation of the relationship mutation boundary.
/// Enforces idempotency replay, detects semantic conflicts via canonical SHA-256 fingerprint,
/// protects against concurrent lost updates via optimistic concurrency, and writes to the audit ledger.
/// </summary>
public sealed class CharacterRelationshipTransitionService : ICharacterRelationshipTransitionService
{
    private const int MaxConcurrencyRetries = 3;

    private readonly CoreDbContext _dbContext;
    private readonly ILogger<CharacterRelationshipTransitionService> _logger;

    public CharacterRelationshipTransitionService(
        CoreDbContext dbContext,
        ILogger<CharacterRelationshipTransitionService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterRelationshipFeedback?> ApplyTransitionAsync(
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
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty) throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        if (executionId == Guid.Empty) throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));
        if (targetId == Guid.Empty) throw new ArgumentException("TargetId cannot be empty.", nameof(targetId));

        var expectedFingerprint = CanonicalRelationshipFingerprint.Compute(
            characterId,
            executionId,
            targetId,
            targetType,
            trustDelta,
            affectionDelta,
            familiarityDelta,
            newRelationshipType);

        // 1. Idempotency Check: query transition ledger for (CharacterId, ExecutionId)
        var existingTransition = await _dbContext.CharacterRelationshipTransitions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.CharacterId == characterId && t.ExecutionId == executionId, ct);

        if (existingTransition != null)
        {
            if (existingTransition.TransitionFingerprint != expectedFingerprint)
            {
                _logger.LogWarning(
                    "[CharacterRelationshipTransitionService] Idempotency conflict for CharacterId={CharacterId}, ExecutionId={ExecutionId}. Existing fingerprint '{Existing}' != incoming '{Incoming}'.",
                    characterId, executionId, existingTransition.TransitionFingerprint, expectedFingerprint);

                throw new CharacterRelationshipIdempotencyConflictException(
                    $"ExecutionId '{executionId}' has already been processed with a different semantic relationship feedback payload.");
            }

            _logger.LogInformation(
                "[CharacterRelationshipTransitionService] Idempotent duplicate transition suppressed for CharacterId={CharacterId}, ExecutionId={ExecutionId}. Reusing recorded feedback.",
                characterId, executionId);

            var rel = await _dbContext.CharacterRelationships
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.CharacterId == characterId && r.TargetType == targetType && r.TargetId == targetId && !r.IsSoftDeleted, ct);

            return new CharacterRelationshipFeedback(
                RelationshipId: rel?.Id ?? Guid.Empty,
                CharacterId: characterId,
                ExecutionId: executionId,
                TargetId: targetId,
                TargetType: targetType,
                TrustDelta: existingTransition.TrustDelta,
                AffectionDelta: existingTransition.AffectionDelta,
                FamiliarityDelta: existingTransition.FamiliarityDelta,
                NewRelationshipType: existingTransition.NewRelationshipType != existingTransition.OldRelationshipType ? existingTransition.NewRelationshipType : null,
                Reason: existingTransition.Reason,
                OccurredAtUtc: new DateTimeOffset(existingTransition.AppliedAtUtc, TimeSpan.Zero)
            );
        }

        // 2. Load or create the authoritative relationship aggregate with concurrency retry
        for (var attempt = 1; attempt <= MaxConcurrencyRetries; attempt++)
        {
            try
            {
                var relationship = await _dbContext.CharacterRelationships
                    .FirstOrDefaultAsync(r => r.CharacterId == characterId && r.TargetType == targetType && r.TargetId == targetId && !r.IsSoftDeleted, ct);

                if (relationship == null)
                {
                    relationship = CharacterRelationship.Create(
                        characterId: characterId,
                        targetType: targetType,
                        targetId: targetId,
                        relationshipType: RelationshipType.Stranger,
                        trust: 0,
                        affection: 0,
                        familiarity: 0);

                    await _dbContext.CharacterRelationships.AddAsync(relationship, ct);
                }

                var versionBefore = relationship.Version;
                var oldType = relationship.RelationshipType;

                // Apply bounded deltas via domain methods
                if (trustDelta != 0) relationship.ApplyTrustDelta(trustDelta, occurredAtUtc.UtcDateTime);
                if (affectionDelta != 0) relationship.ApplyAffectionDelta(affectionDelta, occurredAtUtc.UtcDateTime);
                if (familiarityDelta != 0) relationship.ApplyFamiliarityDelta(familiarityDelta, occurredAtUtc.UtcDateTime);
                if (newRelationshipType.HasValue && newRelationshipType.Value != oldType)
                {
                    relationship.ChangeRelationshipType(newRelationshipType.Value, occurredAtUtc.UtcDateTime);
                }

                var versionAfter = relationship.Version;

                var transition = new CharacterRelationshipTransition(
                    characterId: characterId,
                    executionId: executionId,
                    targetId: targetId,
                    targetType: targetType,
                    trustDelta: trustDelta,
                    affectionDelta: affectionDelta,
                    familiarityDelta: familiarityDelta,
                    oldRelationshipType: oldType,
                    newRelationshipType: newRelationshipType ?? oldType,
                    versionBefore: versionBefore,
                    versionAfter: versionAfter,
                    reason: reason,
                    appliedAtUtc: occurredAtUtc.UtcDateTime
                );

                await _dbContext.CharacterRelationshipTransitions.AddAsync(transition, ct);
                await _dbContext.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "[CharacterRelationshipTransitionService] Successfully recorded relationship transition for CharacterId={CharacterId}, TargetId={TargetId}, ExecutionId={ExecutionId}. Version: {VBefore}->{VAfter}.",
                    characterId, targetId, executionId, versionBefore, versionAfter);

                return new CharacterRelationshipFeedback(
                    RelationshipId: relationship.Id,
                    CharacterId: characterId,
                    ExecutionId: executionId,
                    TargetId: targetId,
                    TargetType: targetType,
                    TrustDelta: trustDelta,
                    AffectionDelta: affectionDelta,
                    FamiliarityDelta: familiarityDelta,
                    NewRelationshipType: newRelationshipType,
                    Reason: reason,
                    OccurredAtUtc: occurredAtUtc
                );
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxConcurrencyRetries)
            {
                _logger.LogWarning(ex,
                    "[CharacterRelationshipTransitionService] Concurrency conflict on attempt {Attempt} for CharacterId={CharacterId}, TargetId={TargetId}. Retrying...",
                    attempt, characterId, targetId);

                // Clear tracked entries to reload fresh from database
                _dbContext.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex,
                    "[CharacterRelationshipTransitionService] Database update exception for CharacterId={CharacterId}, ExecutionId={ExecutionId}. Checking for concurrent execution race.",
                    characterId, executionId);

                _dbContext.ChangeTracker.Clear();

                var concurrentTransition = await _dbContext.CharacterRelationshipTransitions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.CharacterId == characterId && t.ExecutionId == executionId, ct);

                if (concurrentTransition != null)
                {
                    if (concurrentTransition.TransitionFingerprint != expectedFingerprint)
                    {
                        throw new CharacterRelationshipIdempotencyConflictException(
                            $"ExecutionId '{executionId}' has already been processed with a different semantic relationship feedback payload.");
                    }

                    var rel = await _dbContext.CharacterRelationships
                        .AsNoTracking()
                        .FirstOrDefaultAsync(r => r.CharacterId == characterId && r.TargetType == targetType && r.TargetId == targetId && !r.IsSoftDeleted, ct);

                    return new CharacterRelationshipFeedback(
                        RelationshipId: rel?.Id ?? Guid.Empty,
                        CharacterId: characterId,
                        ExecutionId: executionId,
                        TargetId: targetId,
                        TargetType: targetType,
                        TrustDelta: concurrentTransition.TrustDelta,
                        AffectionDelta: concurrentTransition.AffectionDelta,
                        FamiliarityDelta: concurrentTransition.FamiliarityDelta,
                        NewRelationshipType: concurrentTransition.NewRelationshipType != concurrentTransition.OldRelationshipType ? concurrentTransition.NewRelationshipType : null,
                        Reason: concurrentTransition.Reason,
                        OccurredAtUtc: new DateTimeOffset(concurrentTransition.AppliedAtUtc, TimeSpan.Zero)
                    );
                }

                throw;
            }
        }

        _logger.LogError(
            "[CharacterRelationshipTransitionService] Concurrency retries exhausted for CharacterId={CharacterId}, TargetId={TargetId}, ExecutionId={ExecutionId}.",
            characterId, targetId, executionId);

        return null;
    }
}
