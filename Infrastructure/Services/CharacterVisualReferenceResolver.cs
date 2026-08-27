using Application.DTOs;
using Application.Interfaces;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class CharacterVisualReferenceResolver : ICharacterVisualReferenceResolver
{
    private readonly ProjectDbContext _dbContext;
    private readonly ILogger<CharacterVisualReferenceResolver> _logger;

    public CharacterVisualReferenceResolver(
        ProjectDbContext dbContext,
        ILogger<CharacterVisualReferenceResolver> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<VisualReferenceSet> ResolveAsync(Guid characterId, VisualReferenceContext context, CancellationToken ct = default)
    {
        var profile = await _dbContext.CharacterVisualProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

        var profileVersion = profile?.VisualVersion ?? 1;

        // 1. Authoritative Canonical Reference Query (Single-row fetch, strictly authoritative)
        var canonicalEntity = await _dbContext.CharacterVisualReferences
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.CharacterId == characterId && r.IsCanonical && r.Status == VisualReferenceStatus.Active, ct);

        ResolvedReference? primaryRef = null;
        if (canonicalEntity != null)
        {
            float score = 1000f + (canonicalEntity.Priority * 10f);
            primaryRef = new ResolvedReference(
                ReferenceId: canonicalEntity.Id,
                ReferenceUrl: canonicalEntity.ReferenceUrl,
                Type: canonicalEntity.Type,
                IsCanonical: true,
                Priority: canonicalEntity.Priority,
                Score: score,
                SelectionReason: "Active Primary Canonical Identity Reference (Dominating Authority)"
            );
        }
        // Invariant: If no active canonical reference exists, primaryRef remains null.
        // We NEVER promote arbitrary scene references or generated evidence to primary identity authority.

        // 2. Secondary Canonical References Query (Bounded, filtered by type)
        var secondaryEntities = await _dbContext.CharacterVisualReferences
            .AsNoTracking()
            .Where(r => r.CharacterId == characterId && !r.IsCanonical && r.Status == VisualReferenceStatus.Active &&
                        (r.Type == VisualReferenceType.SecondaryCanonical || r.Type == VisualReferenceType.UploadedReference))
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.CreatedAt)
            .Take(context.MaxSecondaryReferences)
            .ToListAsync(ct);

        var secondaryList = secondaryEntities.Select(r => new ResolvedReference(
            ReferenceId: r.Id,
            ReferenceUrl: r.ReferenceUrl,
            Type: r.Type,
            IsCanonical: false,
            Priority: r.Priority,
            Score: (r.Type == VisualReferenceType.SecondaryCanonical ? 500f : 300f) + (r.Priority * 10f),
            SelectionReason: $"Secondary Reference ({r.Type})"
        )).ToList();

        // 3. Scene-specific References Query (Bounded, filtered by type)
        var sceneEntities = await _dbContext.CharacterVisualReferences
            .AsNoTracking()
            .Where(r => r.CharacterId == characterId && !r.IsCanonical && r.Status == VisualReferenceStatus.Active &&
                        (r.Type == VisualReferenceType.SceneReference || r.Type == VisualReferenceType.GeneratedEvidence))
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.CreatedAt)
            .Take(context.MaxSceneReferences)
            .ToListAsync(ct);

        var sceneList = sceneEntities.Select(r => new ResolvedReference(
            ReferenceId: r.Id,
            ReferenceUrl: r.ReferenceUrl,
            Type: r.Type,
            IsCanonical: false,
            Priority: r.Priority,
            Score: (r.Type == VisualReferenceType.SceneReference ? 200f : 100f) + (r.Priority * 5f),
            SelectionReason: $"Scene Evidence Reference ({r.Type})"
        )).ToList();

        var summary = $"Resolved {(primaryRef != null ? 1 : 0)} primary, {secondaryList.Count} secondary, {sceneList.Count} scene references for CharacterId={characterId} (ProfileVersion={profileVersion}).";

        _logger.LogInformation("[CharacterVisualReferenceResolver] {Summary}", summary);

        return new VisualReferenceSet(
            CharacterId: characterId,
            VisualProfileVersion: profileVersion,
            PrimaryIdentityReference: primaryRef,
            SecondaryReferences: secondaryList,
            SceneReferences: sceneList,
            SelectionSummary: summary
        );
    }
}
