using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.CognitiveCycle;
using Domain.Common;
using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.CognitiveCycle;

/// <summary>
/// Infrastructure service orchestrating personality adaptation evidence evaluation,
/// idempotent persistence, evidence accumulation threshold aggregation, and optimistic concurrency trait adaptation.
/// </summary>
public sealed class PersonalityAdaptationService : IPersonalityAdaptationService
{
    public const int DefaultAccumulationThreshold = 3;
    private const int MaxConcurrencyRetries = 3;

    private readonly CoreDbContext _dbContext;
    private readonly IPersonalityAdaptationPolicy _policy;
    private readonly ILogger<PersonalityAdaptationService> _logger;
    private readonly int _threshold;

    public PersonalityAdaptationService(
        CoreDbContext dbContext,
        IPersonalityAdaptationPolicy policy,
        ILogger<PersonalityAdaptationService> logger,
        int threshold = DefaultAccumulationThreshold)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _threshold = threshold <= 0 ? DefaultAccumulationThreshold : threshold;
    }

    public async Task<CharacterPersonalityAdaptationResult?> ProcessAdaptationAsync(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        // 1. Evaluate policy
        var proposal = _policy.Evaluate(context, result);
        if (proposal == null)
        {
            return null;
        }

        var expectedFingerprint = CanonicalPersonalityFingerprint.ComputeEvidence(
            context.CharacterId,
            context.ExecutionId,
            proposal.EvidenceType,
            proposal.TraitKey,
            proposal.Direction,
            proposal.Strength,
            proposal.Reason);

        // 2. Idempotency Check: (CharacterId, ExecutionId)
        var existingEvidence = await _dbContext.CharacterPersonalityAdaptationEvidences
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.CharacterId == context.CharacterId && e.ExecutionId == context.ExecutionId, ct);

        if (existingEvidence != null)
        {
            if (existingEvidence.Fingerprint != expectedFingerprint)
            {
                _logger.LogWarning(
                    "[PersonalityAdaptationService] Idempotency conflict for CharacterId={CharacterId}, ExecutionId={ExecutionId}. Existing '{Existing}' != incoming '{Incoming}'.",
                    context.CharacterId, context.ExecutionId, existingEvidence.Fingerprint, expectedFingerprint);

                throw new PersonalityAdaptationIdempotencyConflictException(
                    $"ExecutionId '{context.ExecutionId}' has already been processed with a different semantic personality adaptation evidence payload.");
            }

            _logger.LogInformation(
                "[PersonalityAdaptationService] Idempotent duplicate evidence suppressed for CharacterId={CharacterId}, ExecutionId={ExecutionId}.",
                context.CharacterId, context.ExecutionId);

            return new CharacterPersonalityAdaptationResult(
                EvidenceId: existingEvidence.Id,
                CharacterId: existingEvidence.CharacterId,
                ExecutionId: existingEvidence.ExecutionId,
                TraitKey: existingEvidence.TraitKey,
                Direction: existingEvidence.Direction,
                Strength: existingEvidence.Strength,
                AdaptationTriggered: existingEvidence.IsApplied
            );
        }

        // 3. Persist new evidence
        var newEvidence = new PersonalityAdaptationEvidence(
            characterId: context.CharacterId,
            executionId: context.ExecutionId,
            evidenceType: proposal.EvidenceType,
            traitKey: proposal.TraitKey,
            direction: proposal.Direction,
            strength: proposal.Strength,
            reason: proposal.Reason,
            fingerprint: expectedFingerprint,
            createdAtUtc: context.TriggeredAtUtc.UtcDateTime
        );

        await _dbContext.CharacterPersonalityAdaptationEvidences.AddAsync(newEvidence, ct);
        await _dbContext.SaveChangesAsync(ct);

        // 4. Evidence Accumulation Check
        var unappliedList = await _dbContext.CharacterPersonalityAdaptationEvidences
            .Where(e => e.CharacterId == context.CharacterId && e.TraitKey == proposal.TraitKey && !e.IsApplied)
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        int netEvidenceScore = unappliedList.Sum(e => e.Direction * e.Strength);

        int adaptationDelta = 0;
        if (netEvidenceScore >= _threshold)
        {
            adaptationDelta = +1;
        }
        else if (netEvidenceScore <= -_threshold)
        {
            adaptationDelta = -1;
        }

        if (adaptationDelta == 0)
        {
            // Below threshold: Evidence recorded, no trait mutation applied
            return new CharacterPersonalityAdaptationResult(
                EvidenceId: newEvidence.Id,
                CharacterId: newEvidence.CharacterId,
                ExecutionId: newEvidence.ExecutionId,
                TraitKey: newEvidence.TraitKey,
                Direction: newEvidence.Direction,
                Strength: newEvidence.Strength,
                AdaptationTriggered: false
            );
        }

        // 5. Apply Adaptation with Optimistic Concurrency Retry
        for (var attempt = 1; attempt <= MaxConcurrencyRetries; attempt++)
        {
            try
            {
                var personality = await _dbContext.CharacterPersonalities
                    .FirstOrDefaultAsync(p => p.CharacterId == context.CharacterId, ct);

                if (personality == null)
                {
                    personality = CharacterPersonality.CreateDefault(context.CharacterId, context.TriggeredAtUtc.UtcDateTime);
                    await _dbContext.CharacterPersonalities.AddAsync(personality, ct);
                }

                var (valueBefore, valueAfter) = personality.AdaptTrait(proposal.TraitKey, adaptationDelta);

                var adaptationFingerprint = CanonicalPersonalityFingerprint.ComputeAdaptation(
                    context.CharacterId,
                    context.ExecutionId,
                    proposal.TraitKey,
                    valueBefore,
                    valueAfter,
                    adaptationDelta,
                    unappliedList.Count);

                var adaptationRecord = new CharacterPersonalityAdaptation(
                    characterId: context.CharacterId,
                    executionId: context.ExecutionId,
                    traitKey: proposal.TraitKey,
                    valueBefore: valueBefore,
                    valueAfter: valueAfter,
                    delta: adaptationDelta,
                    evidenceCount: unappliedList.Count,
                    fingerprint: adaptationFingerprint,
                    createdAtUtc: context.TriggeredAtUtc.UtcDateTime
                );

                await _dbContext.CharacterPersonalityAdaptations.AddAsync(adaptationRecord, ct);

                // Mark unapplied evidence as applied
                foreach (var ev in unappliedList)
                {
                    ev.MarkApplied(adaptationRecord.Id);
                }

                await _dbContext.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "[PersonalityAdaptationService] Applied adaptation for CharacterId={CharacterId}, Trait={Trait}: {Before} -> {After} (Delta={Delta}, Version={Version}).",
                    context.CharacterId, proposal.TraitKey, valueBefore, valueAfter, adaptationDelta, personality.Version);

                return new CharacterPersonalityAdaptationResult(
                    EvidenceId: newEvidence.Id,
                    CharacterId: context.CharacterId,
                    ExecutionId: context.ExecutionId,
                    TraitKey: proposal.TraitKey,
                    Direction: proposal.Direction,
                    Strength: proposal.Strength,
                    AdaptationTriggered: true,
                    TraitValueBefore: valueBefore,
                    TraitValueAfter: valueAfter,
                    TraitDelta: adaptationDelta,
                    AdaptationFingerprint: adaptationFingerprint
                );
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "[PersonalityAdaptationService] Optimistic concurrency conflict on attempt {Attempt}/{MaxAttempts} for CharacterId={CharacterId}. Retrying...",
                    attempt, MaxConcurrencyRetries, context.CharacterId);

                _dbContext.ChangeTracker.Clear();

                if (attempt == MaxConcurrencyRetries)
                {
                    throw;
                }

                await Task.Delay(25 * attempt, ct);
            }
        }

        return new CharacterPersonalityAdaptationResult(
            EvidenceId: newEvidence.Id,
            CharacterId: context.CharacterId,
            ExecutionId: context.ExecutionId,
            TraitKey: proposal.TraitKey,
            Direction: proposal.Direction,
            Strength: proposal.Strength,
            AdaptationTriggered: false
        );
    }
}
