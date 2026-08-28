using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

/// <summary>
/// Service managing visual artifact retention policies, evaluation, and safe asynchronous cleanup.
/// Invariants:
/// - Current active artifacts are protected indefinitely.
/// - Active predecessors referenced by current artifacts are protected.
/// - Artifacts linked to in-flight generation jobs are protected.
/// - Quarantined and orphaned artifacts exceeding TTL based on their lifecycle transition timestamp are eligible for cleanup.
/// </summary>
public sealed class ArtifactRetentionService : IArtifactRetentionService
{
    private readonly CoreDbContext _dbContext;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<ArtifactRetentionService> _logger;

    public ArtifactRetentionService(
        CoreDbContext dbContext,
        IDateTimeProvider dateTimeProvider,
        ILogger<ArtifactRetentionService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ArtifactRetentionEvaluationResult> EvaluateEligibilityAsync(
        Guid artifactId,
        CancellationToken ct = default)
    {
        var artifact = await _dbContext.SceneImages
            .AsNoTracking()
            .FirstOrDefaultAsync(img => img.Id == artifactId, ct);

        if (artifact == null)
        {
            return new ArtifactRetentionEvaluationResult(
                ArtifactId: artifactId,
                IsProtected: false,
                ProtectionReason: "ArtifactNotFound",
                IsEligibleForCleanup: false
            );
        }

        // 1. Protection Check: Is Current Artifact
        if (artifact.IsCurrent || artifact.LifecycleStatus == ArtifactLifecycleStatus.Current)
        {
            return new ArtifactRetentionEvaluationResult(artifactId, true, "CurrentArtifactProtected", false);
        }

        var isSessionCurrent = await _dbContext.VisualSessionStates
            .AsNoTracking()
            .AnyAsync(s => s.CurrentImageId == artifactId, ct);

        if (isSessionCurrent)
        {
            return new ArtifactRetentionEvaluationResult(artifactId, true, "SessionStateCurrentProtected", false);
        }

        // 2. Protection Check: Is Active Predecessor for any current artifact
        var isPredecessorForCurrent = await _dbContext.SceneImages
            .AsNoTracking()
            .AnyAsync(other => other.PredecessorArtifactId == artifactId
                               && other.IsCurrent
                               && other.LifecycleStatus == ArtifactLifecycleStatus.Current, ct);

        if (isPredecessorForCurrent)
        {
            return new ArtifactRetentionEvaluationResult(artifactId, true, "ActivePredecessorProtected", false);
        }

        // 3. Protection Check: Is linked to an in-flight Generation Job
        if (artifact.GenerationJobId.HasValue)
        {
            var isInFlightJob = await _dbContext.ImageGenerationJobs
                .AsNoTracking()
                .AnyAsync(j => j.Id == artifact.GenerationJobId.Value
                               && (j.Status == ImageJobStatus.Processing
                                   || j.Status == ImageJobStatus.Evaluating
                                   || j.Status == ImageJobStatus.Queued), ct);

            if (isInFlightJob)
            {
                return new ArtifactRetentionEvaluationResult(artifactId, true, "InFlightJobProtected", false);
            }
        }

        // 4. Eligible for Cleanup
        return new ArtifactRetentionEvaluationResult(artifactId, false, $"LifecycleStatus:{artifact.LifecycleStatus}", true);
    }

    public async Task<int> CleanupExpiredArtifactsAsync(
        TimeSpan quarantinedTtl,
        TimeSpan orphanTtl,
        CancellationToken ct = default)
    {
        var now = _dateTimeProvider.UtcNow;
        var quarantineCutoff = now.Subtract(quarantinedTtl);
        var orphanCutoff = now.Subtract(orphanTtl);

        // Fetch candidate IDs for quarantined expired artifacts based on QuarantinedAt ?? UpdatedAt ?? CreatedAt
        var quarantinedExpiredIds = await _dbContext.SceneImages
            .Where(img => img.LifecycleStatus == ArtifactLifecycleStatus.Quarantined
                          && ((img.QuarantinedAt != null && img.QuarantinedAt.Value < quarantineCutoff)
                              || (img.QuarantinedAt == null && img.UpdatedAt != null && img.UpdatedAt.Value < quarantineCutoff)
                              || (img.QuarantinedAt == null && img.UpdatedAt == null && img.CreatedAt < quarantineCutoff)))
            .Select(img => img.Id)
            .ToListAsync(ct);

        // Fetch candidate IDs for historical unreferenced artifacts exceeding orphan TTL
        var candidateOrphanIds = await _dbContext.SceneImages
            .Where(img => img.LifecycleStatus == ArtifactLifecycleStatus.Historical
                          && !img.IsCurrent
                          && ((img.UpdatedAt != null && img.UpdatedAt.Value < orphanCutoff)
                              || (img.UpdatedAt == null && img.CreatedAt < orphanCutoff)))
            .Select(img => img.Id)
            .ToListAsync(ct);

        var allCandidateIds = quarantinedExpiredIds.Concat(candidateOrphanIds).Distinct().ToList();
        int cleanedCount = 0;

        foreach (var candidateId in allCandidateIds)
        {
            var eval = await EvaluateEligibilityAsync(candidateId, ct);
            if (eval.IsEligibleForCleanup && !eval.IsProtected)
            {
                var artifact = await _dbContext.SceneImages.FirstOrDefaultAsync(img => img.Id == candidateId, ct);
                if (artifact != null)
                {
                    artifact.MarkDeleted();
                    cleanedCount++;
                }
            }
        }

        if (cleanedCount > 0)
        {
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation("[ArtifactRetentionService] Marked {CleanedCount} expired artifacts as Deleted.", cleanedCount);
        }

        return cleanedCount;
    }
}
