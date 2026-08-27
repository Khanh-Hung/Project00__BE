using Application.DTOs;
using Application.Interfaces;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

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

        // Bounded fetch of active references for this character
        var references = await _dbContext.CharacterVisualReferences
            .AsNoTracking()
            .Where(r => r.CharacterId == characterId && r.Status == VisualReferenceStatus.Active)
            .OrderByDescending(r => r.IsCanonical)
            .ThenByDescending(r => r.Priority)
            .ThenByDescending(r => r.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        ResolvedReference? primaryRef = null;
        var secondaryList = new List<ResolvedReference>();
        var sceneList = new List<ResolvedReference>();

        // 1. Primary Canonical Reference Selection (Dominating priority: 1,000+ points)
        var canonicalEntity = references.FirstOrDefault(r => r.IsCanonical && r.Type == VisualReferenceType.Canonical);

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
        else if (references.Count > 0)
        {
            // Fallback to highest priority active reference if no explicit canonical exists
            var topFallback = references.First();
            float score = 500f + (topFallback.Priority * 10f);
            primaryRef = new ResolvedReference(
                ReferenceId: topFallback.Id,
                ReferenceUrl: topFallback.ReferenceUrl,
                Type: topFallback.Type,
                IsCanonical: false,
                Priority: topFallback.Priority,
                Score: score,
                SelectionReason: "Fallback Highest Priority Reference"
            );
        }

        // 2. Secondary Canonical References Selection
        var remainingCandidates = references
            .Where(r => primaryRef == null || r.Id != primaryRef.ReferenceId)
            .ToList();

        foreach (var candidate in remainingCandidates.Where(r => r.Type == VisualReferenceType.SecondaryCanonical || r.Type == VisualReferenceType.UploadedReference))
        {
            if (secondaryList.Count >= context.MaxSecondaryReferences)
                break;

            float score = (candidate.Type == VisualReferenceType.SecondaryCanonical ? 500f : 300f) + (candidate.Priority * 10f);
            secondaryList.Add(new ResolvedReference(
                ReferenceId: candidate.Id,
                ReferenceUrl: candidate.ReferenceUrl,
                Type: candidate.Type,
                IsCanonical: false,
                Priority: candidate.Priority,
                Score: score,
                SelectionReason: $"Secondary Reference ({candidate.Type})"
            ));
        }

        // 3. Scene-specific References Selection
        foreach (var candidate in remainingCandidates.Where(r => r.Type == VisualReferenceType.SceneReference || r.Type == VisualReferenceType.GeneratedEvidence))
        {
            if (sceneList.Count >= context.MaxSceneReferences)
                break;

            float score = (candidate.Type == VisualReferenceType.SceneReference ? 200f : 100f) + (candidate.Priority * 5f);
            sceneList.Add(new ResolvedReference(
                ReferenceId: candidate.Id,
                ReferenceUrl: candidate.ReferenceUrl,
                Type: candidate.Type,
                IsCanonical: false,
                Priority: candidate.Priority,
                Score: score,
                SelectionReason: $"Scene Evidence Reference ({candidate.Type})"
            ));
        }

        var summary = $"Resolved { (primaryRef != null ? 1 : 0) } primary, {secondaryList.Count} secondary, {sceneList.Count} scene references for CharacterId={characterId} (ProfileVersion={profileVersion}).";

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
