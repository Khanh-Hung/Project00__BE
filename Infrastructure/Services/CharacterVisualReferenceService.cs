using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class CharacterVisualReferenceService : ICharacterVisualReferenceService
{
    private readonly ProjectDbContext _dbContext;
    private readonly ICharacterVisualProfileService _profileService;
    private readonly ILogger<CharacterVisualReferenceService> _logger;

    public CharacterVisualReferenceService(
        ProjectDbContext dbContext,
        ICharacterVisualProfileService profileService,
        ILogger<CharacterVisualReferenceService> logger)
    {
        _dbContext = dbContext;
        _profileService = profileService;
        _logger = logger;
    }

    public async Task<CharacterVisualReference> RegisterReferenceAsync(RegisterVisualReferenceRequest request, CancellationToken ct = default)
    {
        // 1. Idempotency check on ArtifactId
        if (request.ArtifactId.HasValue)
        {
            var existing = await _dbContext.CharacterVisualReferences
                .FirstOrDefaultAsync(r => r.CharacterId == request.CharacterId && r.ArtifactId == request.ArtifactId.Value, ct);

            if (existing != null)
            {
                _logger.LogInformation("[CharacterVisualReferenceService] Reference already exists for CharacterId={CharacterId}, ArtifactId={ArtifactId}. Returning existing.",
                    request.CharacterId, request.ArtifactId.Value);
                return existing;
            }
        }

        var now = DateTime.UtcNow;

        if (request.IsCanonical)
        {
            // Execute in transaction to atomically demote any existing canonical reference
            if (_dbContext.Database.IsRelational())
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);

                await DemoteExistingCanonicalsAsync(request.CharacterId, now, ct);

                var reference = new CharacterVisualReference(
                    characterId: request.CharacterId,
                    referenceUrl: request.ReferenceUrl,
                    type: VisualReferenceType.Canonical,
                    status: request.Status,
                    isCanonical: true,
                    artifactId: request.ArtifactId,
                    priority: request.Priority,
                    sourceGenerationJobId: request.SourceGenerationJobId,
                    sourceVisualRevision: request.SourceVisualRevision,
                    now: now
                );

                await _dbContext.CharacterVisualReferences.AddAsync(reference, ct);
                await _dbContext.SaveChangesAsync(ct);

                // Update visual profile
                await _profileService.SetPrimaryReferenceAsync(request.CharacterId, reference.Id, ct);

                await transaction.CommitAsync(ct);

                _logger.LogInformation("[CharacterVisualReferenceService] Registered new Canonical ReferenceId={ReferenceId} for CharacterId={CharacterId}",
                    reference.Id, request.CharacterId);

                return reference;
            }
            else
            {
                await DemoteExistingCanonicalsAsync(request.CharacterId, now, ct);

                var reference = new CharacterVisualReference(
                    characterId: request.CharacterId,
                    referenceUrl: request.ReferenceUrl,
                    type: VisualReferenceType.Canonical,
                    status: request.Status,
                    isCanonical: true,
                    artifactId: request.ArtifactId,
                    priority: request.Priority,
                    sourceGenerationJobId: request.SourceGenerationJobId,
                    sourceVisualRevision: request.SourceVisualRevision,
                    now: now
                );

                await _dbContext.CharacterVisualReferences.AddAsync(reference, ct);
                await _dbContext.SaveChangesAsync(ct);

                await _profileService.SetPrimaryReferenceAsync(request.CharacterId, reference.Id, ct);

                return reference;
            }
        }
        else
        {
            var reference = new CharacterVisualReference(
                characterId: request.CharacterId,
                referenceUrl: request.ReferenceUrl,
                type: request.Type,
                status: request.Status,
                isCanonical: false,
                artifactId: request.ArtifactId,
                priority: request.Priority,
                sourceGenerationJobId: request.SourceGenerationJobId,
                sourceVisualRevision: request.SourceVisualRevision,
                now: now
            );

            await _dbContext.CharacterVisualReferences.AddAsync(reference, ct);
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("[CharacterVisualReferenceService] Registered ReferenceId={ReferenceId} (Type={Type}) for CharacterId={CharacterId}",
                reference.Id, reference.Type, request.CharacterId);

            return reference;
        }
    }

    public async Task<CharacterVisualReference> PromoteToCanonicalAsync(Guid characterId, Guid referenceId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        if (_dbContext.Database.IsRelational())
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);

            var target = await _dbContext.CharacterVisualReferences
                .FirstOrDefaultAsync(r => r.Id == referenceId && r.CharacterId == characterId, ct);

            if (target == null)
            {
                await transaction.RollbackAsync(ct);
                throw new InvalidOperationException($"Visual reference '{referenceId}' not found for character '{characterId}'.");
            }

            if (target.Status == VisualReferenceStatus.Archived)
            {
                await transaction.RollbackAsync(ct);
                throw new InvalidOperationException($"Cannot promote archived visual reference '{referenceId}'.");
            }

            // Demote existing canonicals
            await DemoteExistingCanonicalsAsync(characterId, now, ct);

            // Promote target
            target.PromoteToCanonical(now);
            await _dbContext.SaveChangesAsync(ct);

            // Update visual profile
            await _profileService.SetPrimaryReferenceAsync(characterId, target.Id, ct);

            await transaction.CommitAsync(ct);

            _logger.LogInformation("[CharacterVisualReferenceService] Promoted ReferenceId={ReferenceId} to Canonical for CharacterId={CharacterId}",
                referenceId, characterId);

            return target;
        }
        else
        {
            var target = await _dbContext.CharacterVisualReferences
                .FirstOrDefaultAsync(r => r.Id == referenceId && r.CharacterId == characterId, ct);

            if (target == null)
            {
                throw new InvalidOperationException($"Visual reference '{referenceId}' not found for character '{characterId}'.");
            }

            if (target.Status == VisualReferenceStatus.Archived)
            {
                throw new InvalidOperationException($"Cannot promote archived visual reference '{referenceId}'.");
            }

            await DemoteExistingCanonicalsAsync(characterId, now, ct);

            target.PromoteToCanonical(now);
            await _dbContext.SaveChangesAsync(ct);

            await _profileService.SetPrimaryReferenceAsync(characterId, target.Id, ct);

            return target;
        }
    }

    public async Task<CharacterVisualReference> DemoteCanonicalAsync(Guid characterId, Guid referenceId, CancellationToken ct = default)
    {
        var target = await _dbContext.CharacterVisualReferences
            .FirstOrDefaultAsync(r => r.Id == referenceId && r.CharacterId == characterId, ct);

        if (target == null)
        {
            throw new InvalidOperationException($"Visual reference '{referenceId}' not found for character '{characterId}'.");
        }

        var now = DateTime.UtcNow;
        target.DemoteCanonical(now);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("[CharacterVisualReferenceService] Demoted Canonical ReferenceId={ReferenceId} for CharacterId={CharacterId}",
            referenceId, characterId);

        return target;
    }

    public async Task<CharacterVisualReference> ArchiveReferenceAsync(Guid characterId, Guid referenceId, CancellationToken ct = default)
    {
        var target = await _dbContext.CharacterVisualReferences
            .FirstOrDefaultAsync(r => r.Id == referenceId && r.CharacterId == characterId, ct);

        if (target == null)
        {
            throw new InvalidOperationException($"Visual reference '{referenceId}' not found for character '{characterId}'.");
        }

        var now = DateTime.UtcNow;
        target.Archive(now);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("[CharacterVisualReferenceService] Archived ReferenceId={ReferenceId} for CharacterId={CharacterId}",
            referenceId, characterId);

        return target;
    }

    public async Task<IReadOnlyList<CharacterVisualReference>> ListReferencesAsync(Guid characterId, VisualReferenceStatus? status = null, CancellationToken ct = default)
    {
        var query = _dbContext.CharacterVisualReferences
            .Where(r => r.CharacterId == characterId);

        if (status.HasValue)
        {
            query = query.Where(r => r.Status == status.Value);
        }

        return await query
            .OrderByDescending(r => r.IsCanonical)
            .ThenByDescending(r => r.Priority)
            .ThenByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
    }

    public async Task<CharacterVisualReference?> GetPrimaryCanonicalReferenceAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterVisualReferences
            .FirstOrDefaultAsync(r => r.CharacterId == characterId && r.IsCanonical && r.Type == VisualReferenceType.Canonical && r.Status == VisualReferenceStatus.Active, ct);
    }

    private async Task DemoteExistingCanonicalsAsync(Guid characterId, DateTime now, CancellationToken ct)
    {
        var existingCanonicals = await _dbContext.CharacterVisualReferences
            .Where(r => r.CharacterId == characterId && r.IsCanonical)
            .ToListAsync(ct);

        foreach (var canonical in existingCanonicals)
        {
            canonical.DemoteCanonical(now);
        }
    }
}
