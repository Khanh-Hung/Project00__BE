using Application.Common;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Reconciles orphan or unreferenced generation artifacts.
/// Ensures unverified candidate artifacts generated before a worker crash are never promoted to IsCurrent,
/// preserves historical audit lineages, and atomically demotes illegal unaccepted current flags.
/// </summary>
public sealed class ArtifactReconciliationService : IArtifactReconciliationService
{
    private readonly CoreDbContext _dbContext;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<ArtifactReconciliationService> _logger;

    public ArtifactReconciliationService(
        CoreDbContext dbContext,
        IDateTimeProvider dateTimeProvider,
        ILogger<ArtifactReconciliationService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Scans and reconciles artifact anomalies:
    /// 1. Demotes any SceneImage where IsCurrent=true but the owning ImageGenerationJob was Failed, Cancelled, or unaccepted.
    /// 2. Uses atomic DB-level conditional updates to prevent race conditions with concurrent acceptance commits.
    /// </summary>
    public async Task<int> ReconcileOrphanArtifactsAsync(CancellationToken ct = default)
    {
        var now = _dateTimeProvider.UtcNow;
        int demotedCount = 0;

        if (_dbContext.Database.IsRelational())
        {
            // Database-level conditional CAS:
            // Atomically demote SceneImages where IsCurrent = true IF AND ONLY IF the parent ImageGenerationJob
            // in the database currently is Failed, Cancelled, or has AcceptedAttemptId == null.
            demotedCount = await _dbContext.SceneImages
                .Where(img => img.IsCurrent
                              && _dbContext.ImageGenerationJobs.Any(j => j.Id == img.GenerationJobId
                                                                        && (j.Status == ImageJobStatus.Failed 
                                                                            || j.Status == ImageJobStatus.Cancelled 
                                                                            || j.AcceptedAttemptId == null)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(img => img.IsCurrent, false)
                    .SetProperty(img => img.UpdatedAt, now), ct);

            if (demotedCount > 0)
            {
                _logger.LogWarning("[ArtifactReconciliation] Atomically demoted {Count} orphan/invalid current artifacts.", demotedCount);
                GenerationObservability.OrphanArtifactsTotal.Add(demotedCount);
            }

            return demotedCount;
        }
        else
        {
            // In-memory / Non-relational test path:
            // Re-verify authoritative job state immediately before mutating the entity to avoid race condition with concurrent acceptance
            var candidateImages = await _dbContext.SceneImages
                .Where(img => img.IsCurrent)
                .ToListAsync(ct);

            foreach (var img in candidateImages)
            {
                var freshJob = await _dbContext.ImageGenerationJobs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(j => j.Id == img.GenerationJobId, ct);

                if (freshJob != null && (freshJob.Status == ImageJobStatus.Failed 
                                         || freshJob.Status == ImageJobStatus.Cancelled 
                                         || freshJob.AcceptedAttemptId == null))
                {
                    img.DemoteCurrent();
                    demotedCount++;
                }
            }

            if (demotedCount > 0)
            {
                await _dbContext.SaveChangesAsync(ct);
                _logger.LogWarning("[ArtifactReconciliation] Demoted {Count} orphan/invalid current artifacts.", demotedCount);
                GenerationObservability.OrphanArtifactsTotal.Add(demotedCount);
            }

            return demotedCount;
        }
    }
}
