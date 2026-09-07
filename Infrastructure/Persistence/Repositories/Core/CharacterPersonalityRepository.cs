using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.CognitiveCycle;
using Domain.Common;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public sealed class CharacterPersonalityRepository : ICharacterPersonalityRepository
{
    private readonly CoreDbContext _dbContext;

    public CharacterPersonalityRepository(CoreDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<CharacterPersonality?> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty) return null;

        return await _dbContext.CharacterPersonalities
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);
    }

    public async Task<CharacterPersonality> GetOrCreateDefaultAsync(Guid characterId, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        var existing = await _dbContext.CharacterPersonalities
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

        if (existing != null)
        {
            return existing;
        }

        var newPersonality = CharacterPersonality.CreateDefault(characterId);

        try
        {
            await _dbContext.CharacterPersonalities.AddAsync(newPersonality, ct);
            await _dbContext.SaveChangesAsync(ct);
            return newPersonality;
        }
        catch (DbUpdateException)
        {
            // Clean up locally created entity to keep ChangeTracker pristine
            _dbContext.Entry(newPersonality).State = EntityState.Detached;

            var winner = await _dbContext.CharacterPersonalities
                .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

            if (winner != null)
            {
                return winner;
            }

            throw;
        }
    }

    public async Task AddAsync(CharacterPersonality personality, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(personality);
        await _dbContext.CharacterPersonalities.AddAsync(personality, ct);
    }

    public async Task<PersonalityAdaptationEvidence?> GetEvidenceByExecutionIdAsync(
        Guid characterId,
        Guid executionId,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty || executionId == Guid.Empty) return null;

        return await _dbContext.CharacterPersonalityAdaptationEvidences
            .FirstOrDefaultAsync(e => e.CharacterId == characterId && e.ExecutionId == executionId, ct);
    }

    public async Task<PersonalityAdaptationEvidence?> GetEvidenceByExecutionAndTraitAsync(
        Guid characterId,
        Guid executionId,
        string traitKey,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty || executionId == Guid.Empty) return null;

        var normalizedTraitKey = PersonalityTraitKeys.Normalize(traitKey);

        return await _dbContext.CharacterPersonalityAdaptationEvidences
            .FirstOrDefaultAsync(e => e.CharacterId == characterId && e.ExecutionId == executionId && e.TraitKey == normalizedTraitKey, ct);
    }

    public async Task<PersonalityAdaptationEvidence> AddOrGetEvidenceAsync(
        PersonalityAdaptationEvidence evidence,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var existing = await GetEvidenceByExecutionAndTraitAsync(evidence.CharacterId, evidence.ExecutionId, evidence.TraitKey, ct);
        if (existing != null)
        {
            if (existing.Fingerprint == evidence.Fingerprint)
            {
                return existing;
            }

            throw new PersonalityAdaptationIdempotencyConflictException(
                $"Idempotency conflict detected for CharacterId={evidence.CharacterId}, ExecutionId={evidence.ExecutionId}, TraitKey={evidence.TraitKey}. " +
                $"Existing fingerprint '{existing.Fingerprint}' does not match incoming fingerprint '{evidence.Fingerprint}'.");
        }

        try
        {
            await _dbContext.CharacterPersonalityAdaptationEvidences.AddAsync(evidence, ct);
            await _dbContext.SaveChangesAsync(ct);
            return evidence;
        }
        catch (DbUpdateException)
        {
            // Concurrent insert race (P0-3): detach candidate and query authoritative row
            _dbContext.Entry(evidence).State = EntityState.Detached;

            var normalizedTraitKey = PersonalityTraitKeys.Normalize(evidence.TraitKey);
            var concurrent = await _dbContext.CharacterPersonalityAdaptationEvidences
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.CharacterId == evidence.CharacterId && e.ExecutionId == evidence.ExecutionId && e.TraitKey == normalizedTraitKey, ct);

            if (concurrent != null)
            {
                if (concurrent.Fingerprint == evidence.Fingerprint)
                {
                    return concurrent;
                }

                throw new PersonalityAdaptationIdempotencyConflictException(
                    $"Concurrent idempotency conflict detected for CharacterId={evidence.CharacterId}, ExecutionId={evidence.ExecutionId}, TraitKey={evidence.TraitKey}. " +
                    $"Committed fingerprint '{concurrent.Fingerprint}' does not match candidate fingerprint '{evidence.Fingerprint}'.");
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<PersonalityAdaptationEvidence>> GetUnappliedEvidenceAsync(
        Guid characterId,
        string traitKey,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty) return [];

        var normalizedTraitKey = PersonalityTraitKeys.Normalize(traitKey);

        return await _dbContext.CharacterPersonalityAdaptationEvidences
            .Where(e => e.CharacterId == characterId && e.TraitKey == normalizedTraitKey && !e.IsApplied)
            .OrderBy(e => e.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<CharacterPersonalityAdaptation?> GetAdaptationByExecutionIdAsync(
        Guid characterId,
        Guid executionId,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty || executionId == Guid.Empty) return null;

        return await _dbContext.CharacterPersonalityAdaptations
            .FirstOrDefaultAsync(a => a.CharacterId == characterId && a.ExecutionId == executionId, ct);
    }

    public async Task<CharacterPersonalityAdaptation?> GetAdaptationByExecutionAndTraitAsync(
        Guid characterId,
        Guid executionId,
        string traitKey,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty || executionId == Guid.Empty) return null;

        var normalizedTraitKey = PersonalityTraitKeys.Normalize(traitKey);

        return await _dbContext.CharacterPersonalityAdaptations
            .FirstOrDefaultAsync(a => a.CharacterId == characterId && a.ExecutionId == executionId && a.TraitKey == normalizedTraitKey, ct);
    }

    public async Task<CharacterPersonalityAdaptation> AddOrGetAdaptationAsync(
        CharacterPersonalityAdaptation adaptation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adaptation);

        var existing = await GetAdaptationByExecutionAndTraitAsync(adaptation.CharacterId, adaptation.ExecutionId, adaptation.TraitKey, ct);
        if (existing != null)
        {
            if (existing.Fingerprint == adaptation.Fingerprint)
            {
                return existing;
            }

            throw new PersonalityAdaptationIdempotencyConflictException(
                $"Idempotency conflict detected for adaptation CharacterId={adaptation.CharacterId}, ExecutionId={adaptation.ExecutionId}, TraitKey={adaptation.TraitKey}. " +
                $"Existing fingerprint '{existing.Fingerprint}' does not match incoming fingerprint '{adaptation.Fingerprint}'.");
        }

        try
        {
            await _dbContext.CharacterPersonalityAdaptations.AddAsync(adaptation, ct);
            await _dbContext.SaveChangesAsync(ct);
            return adaptation;
        }
        catch (DbUpdateException)
        {
            // Concurrent insert race: detach candidate and query authoritative row
            _dbContext.Entry(adaptation).State = EntityState.Detached;

            var normalizedTraitKey = PersonalityTraitKeys.Normalize(adaptation.TraitKey);
            var concurrent = await _dbContext.CharacterPersonalityAdaptations
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.CharacterId == adaptation.CharacterId && a.ExecutionId == adaptation.ExecutionId && a.TraitKey == normalizedTraitKey, ct);

            if (concurrent != null)
            {
                if (concurrent.Fingerprint == adaptation.Fingerprint)
                {
                    return concurrent;
                }

                throw new PersonalityAdaptationIdempotencyConflictException(
                    $"Concurrent idempotency conflict detected for adaptation CharacterId={adaptation.CharacterId}, ExecutionId={adaptation.ExecutionId}, TraitKey={adaptation.TraitKey}. " +
                    $"Committed fingerprint '{concurrent.Fingerprint}' does not match candidate fingerprint '{adaptation.Fingerprint}'.");
            }

            throw;
        }
    }

    public async Task AddAdaptationAsync(
        CharacterPersonalityAdaptation adaptation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adaptation);

        var expectedFingerprint = CanonicalPersonalityFingerprint.ComputeAdaptation(
            adaptation.CharacterId,
            adaptation.ExecutionId,
            adaptation.TraitKey,
            adaptation.ValueBefore,
            adaptation.ValueAfter,
            adaptation.Delta,
            adaptation.EvidenceCount);

        if (adaptation.Fingerprint != expectedFingerprint)
        {
            throw new PersonalityAdaptationIdempotencyConflictException(
                $"Adaptation fingerprint mismatch for CharacterId={adaptation.CharacterId}, ExecutionId={adaptation.ExecutionId}, TraitKey={adaptation.TraitKey}. " +
                $"Expected '{expectedFingerprint}', got '{adaptation.Fingerprint}'.");
        }

        await _dbContext.CharacterPersonalityAdaptations.AddAsync(adaptation, ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _dbContext.SaveChangesAsync(ct);
    }

    public void ClearTracking()
    {
        _dbContext.ChangeTracker.Clear();
    }
}
