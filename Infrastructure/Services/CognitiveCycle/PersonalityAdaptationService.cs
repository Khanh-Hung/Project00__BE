using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.CognitiveCycle;
using Domain.Common;
using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
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

    private readonly ICharacterPersonalityRepository _repository;
    private readonly IPersonalityAdaptationPolicy _policy;
    private readonly ILogger<PersonalityAdaptationService> _logger;
    private readonly int _threshold;

    public PersonalityAdaptationService(
        ICharacterPersonalityRepository repository,
        IPersonalityAdaptationPolicy policy,
        ILogger<PersonalityAdaptationService> logger,
        int threshold = DefaultAccumulationThreshold)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _threshold = threshold <= 0 ? DefaultAccumulationThreshold : threshold;
    }

    public PersonalityAdaptationService(
        CoreDbContext dbContext,
        IPersonalityAdaptationPolicy policy,
        ILogger<PersonalityAdaptationService> logger,
        int threshold = DefaultAccumulationThreshold)
        : this(new CharacterPersonalityRepository(dbContext), policy, logger, threshold)
    {
    }

    public async Task<IReadOnlyList<CharacterPersonalityAdaptationResult>> ProcessAdaptationsAsync(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        // 1. Evaluate policy
        var proposals = _policy.Evaluate(context, result);
        if (proposals == null || proposals.Count == 0)
        {
            return Array.Empty<CharacterPersonalityAdaptationResult>();
        }

        var results = new List<CharacterPersonalityAdaptationResult>();

        foreach (var proposal in proposals)
        {
            var singleResult = await ProcessSingleProposalAsync(context, proposal, ct);
            if (singleResult != null)
            {
                results.Add(singleResult);
            }
            _repository.ClearTracking();
        }

        return results;
    }

    public async Task<CharacterPersonalityAdaptationResult?> ProcessAdaptationAsync(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result,
        CancellationToken ct = default)
    {
        var results = await ProcessAdaptationsAsync(context, result, ct);
        return results.FirstOrDefault(r => r.AdaptationTriggered) ?? results.FirstOrDefault();
    }

    private async Task<CharacterPersonalityAdaptationResult?> ProcessSingleProposalAsync(
        CharacterCognitiveCycleContext context,
        PersonalityAdaptationProposal proposal,
        CancellationToken ct)
    {
        // Compute canonical SHA-256 fingerprint from machine-level fields (Reason excluded)
        var expectedFingerprint = CanonicalPersonalityFingerprint.ComputeEvidence(
            context.CharacterId,
            context.ExecutionId,
            proposal.EvidenceType,
            proposal.TraitKey,
            proposal.Direction,
            proposal.Strength);

        // 2. Persist Evidence atomically with idempotent race handling (P0-3)
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

        var evidence = await _repository.AddOrGetEvidenceAsync(newEvidence, ct);

        // 3. Evidence Accumulation & Adaptation Loop (P0-2)
        for (var attempt = 1; attempt <= MaxConcurrencyRetries; attempt++)
        {
            // If retry > 1, clear tracking to avoid stale detached entities (P2-1: directly via repository abstraction)
            if (attempt > 1)
            {
                _repository.ClearTracking();
            }

            // Check if an adaptation for this ExecutionId and TraitKey was already committed
            var existingAdaptation = await _repository.GetAdaptationByExecutionAndTraitAsync(context.CharacterId, context.ExecutionId, proposal.TraitKey, ct);
            if (existingAdaptation != null)
            {
                var canonicalFingerprint = CanonicalPersonalityFingerprint.ComputeAdaptation(
                    existingAdaptation.CharacterId,
                    existingAdaptation.ExecutionId,
                    existingAdaptation.TraitKey,
                    existingAdaptation.ValueBefore,
                    existingAdaptation.ValueAfter,
                    existingAdaptation.Delta,
                    existingAdaptation.EvidenceCount);

                if (existingAdaptation.Fingerprint != canonicalFingerprint)
                {
                    throw new PersonalityAdaptationIdempotencyConflictException(
                        $"Idempotency conflict detected for CharacterId={context.CharacterId}, ExecutionId={context.ExecutionId}, TraitKey={proposal.TraitKey}. " +
                        $"Existing adaptation fingerprint '{existingAdaptation.Fingerprint}' does not match canonical payload fingerprint '{canonicalFingerprint}'.");
                }

                return new CharacterPersonalityAdaptationResult(
                    EvidenceId: evidence.Id,
                    CharacterId: context.CharacterId,
                    ExecutionId: context.ExecutionId,
                    TraitKey: existingAdaptation.TraitKey,
                    Direction: evidence.Direction,
                    Strength: evidence.Strength,
                    AdaptationTriggered: true,
                    TraitValueBefore: existingAdaptation.ValueBefore,
                    TraitValueAfter: existingAdaptation.ValueAfter,
                    TraitDelta: existingAdaptation.Delta,
                    AdaptationFingerprint: existingAdaptation.Fingerprint
                );
            }

            // Query fresh unapplied evidence from DB
            var unappliedList = await _repository.GetUnappliedEvidenceAsync(context.CharacterId, proposal.TraitKey, ct);

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
                // Below threshold or concurrent worker already consumed evidence: No adaptation
                return new CharacterPersonalityAdaptationResult(
                    EvidenceId: evidence.Id,
                    CharacterId: context.CharacterId,
                    ExecutionId: context.ExecutionId,
                    TraitKey: proposal.TraitKey,
                    Direction: evidence.Direction,
                    Strength: evidence.Strength,
                    AdaptationTriggered: false
                );
            }

            // Select only the threshold number of unapplied evidence items contributing to this direction
            var targetDirection = adaptationDelta > 0 ? 1 : -1;
            var toApply = unappliedList.Where(e => e.Direction == targetDirection).Take(_threshold).ToList();

            if (toApply.Count < _threshold)
            {
                return new CharacterPersonalityAdaptationResult(
                    EvidenceId: evidence.Id,
                    CharacterId: context.CharacterId,
                    ExecutionId: context.ExecutionId,
                    TraitKey: proposal.TraitKey,
                    Direction: evidence.Direction,
                    Strength: evidence.Strength,
                    AdaptationTriggered: false
                );
            }

            // Load fresh personality from repository
            var personality = await _repository.GetOrCreateDefaultAsync(context.CharacterId, ct);
            var (valueBefore, valueAfter) = personality.AdaptTrait(proposal.TraitKey, adaptationDelta);

            var adaptationFingerprint = CanonicalPersonalityFingerprint.ComputeAdaptation(
                context.CharacterId,
                context.ExecutionId,
                proposal.TraitKey,
                valueBefore,
                valueAfter,
                adaptationDelta,
                toApply.Count);

            var adaptationRecord = new CharacterPersonalityAdaptation(
                characterId: context.CharacterId,
                executionId: context.ExecutionId,
                traitKey: proposal.TraitKey,
                valueBefore: valueBefore,
                valueAfter: valueAfter,
                delta: adaptationDelta,
                evidenceCount: toApply.Count,
                fingerprint: adaptationFingerprint,
                createdAtUtc: context.TriggeredAtUtc.UtcDateTime
            );

            await _repository.AddAdaptationAsync(adaptationRecord, ct);

            // Mark the claimed evidence items
            foreach (var ev in toApply)
            {
                ev.MarkApplied(adaptationRecord.Id);
            }

            try
            {
                await _repository.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "[PersonalityAdaptationService] Applied adaptation for CharacterId={CharacterId}, Trait={Trait}: {Before} -> {After} (Delta={Delta}, Version={Version}).",
                    context.CharacterId, proposal.TraitKey, valueBefore, valueAfter, adaptationDelta, personality.Version);

                return new CharacterPersonalityAdaptationResult(
                    EvidenceId: evidence.Id,
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
                    "[PersonalityAdaptationService] Optimistic concurrency conflict on attempt {Attempt}/{MaxAttempts} for CharacterId={CharacterId}. Reloading fresh evidence...",
                    attempt, MaxConcurrencyRetries, context.CharacterId);

                _repository.ClearTracking();

                if (attempt == MaxConcurrencyRetries)
                {
                    // If retries exhausted, check if another worker already committed adaptation
                    var committed = await _repository.GetAdaptationByExecutionAndTraitAsync(context.CharacterId, context.ExecutionId, proposal.TraitKey, ct);
                    if (committed != null)
                    {
                        if (committed.Fingerprint != adaptationFingerprint)
                        {
                            throw new PersonalityAdaptationIdempotencyConflictException(
                                $"Concurrent idempotency conflict detected on retry exhaustion for adaptation CharacterId={context.CharacterId}, ExecutionId={context.ExecutionId}, TraitKey={proposal.TraitKey}. " +
                                $"Committed fingerprint '{committed.Fingerprint}' does not match candidate fingerprint '{adaptationFingerprint}'.");
                        }

                        return new CharacterPersonalityAdaptationResult(
                            EvidenceId: evidence.Id,
                            CharacterId: context.CharacterId,
                            ExecutionId: context.ExecutionId,
                            TraitKey: committed.TraitKey,
                            Direction: evidence.Direction,
                            Strength: evidence.Strength,
                            AdaptationTriggered: true,
                            TraitValueBefore: committed.ValueBefore,
                            TraitValueAfter: committed.ValueAfter,
                            TraitDelta: committed.Delta,
                            AdaptationFingerprint: committed.Fingerprint
                        );
                    }

                    return new CharacterPersonalityAdaptationResult(
                        EvidenceId: evidence.Id,
                        CharacterId: context.CharacterId,
                        ExecutionId: context.ExecutionId,
                        TraitKey: proposal.TraitKey,
                        Direction: proposal.Direction,
                        Strength: proposal.Strength,
                        AdaptationTriggered: false
                    );
                }

                await Task.Delay(25 * attempt, ct);
            }
            catch (DbUpdateException)
            {
                _repository.ClearTracking();
                // Unique constraint race on (CharacterId, ExecutionId, TraitKey) adaptation
                var existing = await _repository.GetAdaptationByExecutionAndTraitAsync(context.CharacterId, context.ExecutionId, proposal.TraitKey, ct);
                if (existing != null)
                {
                    if (existing.Fingerprint != adaptationFingerprint)
                    {
                        throw new PersonalityAdaptationIdempotencyConflictException(
                            $"Concurrent idempotency conflict detected for adaptation CharacterId={context.CharacterId}, ExecutionId={context.ExecutionId}, TraitKey={proposal.TraitKey}. " +
                            $"Committed fingerprint '{existing.Fingerprint}' does not match candidate fingerprint '{adaptationFingerprint}'.");
                    }

                    return new CharacterPersonalityAdaptationResult(
                        EvidenceId: evidence.Id,
                        CharacterId: context.CharacterId,
                        ExecutionId: context.ExecutionId,
                        TraitKey: existing.TraitKey,
                        Direction: evidence.Direction,
                        Strength: evidence.Strength,
                        AdaptationTriggered: true,
                        TraitValueBefore: existing.ValueBefore,
                        TraitValueAfter: existing.ValueAfter,
                        TraitDelta: existing.Delta,
                        AdaptationFingerprint: existing.Fingerprint
                    );
                }

                throw;
            }
        }

        return new CharacterPersonalityAdaptationResult(
            EvidenceId: evidence.Id,
            CharacterId: context.CharacterId,
            ExecutionId: context.ExecutionId,
            TraitKey: proposal.TraitKey,
            Direction: proposal.Direction,
            Strength: proposal.Strength,
            AdaptationTriggered: false
        );
    }

}
