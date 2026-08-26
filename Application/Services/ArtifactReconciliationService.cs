using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

/// <summary>
/// Reconciles orphan or unreferenced generation artifacts.
/// Ensures unverified candidate artifacts generated before a worker crash are never promoted to IsCurrent,
/// preserves historical audit lineages, and demotes illegal unaccepted current flags.
/// </summary>
public sealed class ArtifactReconciliationService
{
    private readonly ProjectDbContext _dbContext;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<ArtifactReconciliationService> _logger;

    public ArtifactReconciliationService(
        ProjectDbContext dbContext,
        IDateTimeProvider dateTimeProvider,
        ILogger<ArtifactReconciliationService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Scans and reconciles artifact anomalies:
    /// 1. Demotes any SceneImage where IsCurrent=true but the owning ImageGenerationJob was Failed or Cancelled.
    /// 2. Ensures unaccepted attempts never have current artifacts.
    /// </summary>
    public async Task<int> ReconcileOrphanArtifactsAsync(CancellationToken ct = default)
    {
        var now = _dateTimeProvider.UtcNow;

        // Query active images whose generation job is in a non-completed terminal state (Failed or Cancelled)
        var invalidCurrentImages = await _dbContext.SceneImages
            .Where(img => img.IsCurrent)
            .Join(
                _dbContext.ImageGenerationJobs.Where(j => j.Status == ImageJobStatus.Failed || j.Status == ImageJobStatus.Cancelled || j.AcceptedAttemptId == null),
                img => img.GenerationJobId,
                job => job.Id,
                (img, job) => img
            )
            .ToListAsync(ct);

        if (invalidCurrentImages.Count == 0)
            return 0;

        _logger.LogWarning("[ArtifactReconciliation] Found {Count} orphan/invalid current artifacts to demote.", invalidCurrentImages.Count);

        int demotedCount = 0;
        foreach (var img in invalidCurrentImages)
        {
            img.DemoteCurrent();
            demotedCount++;
        }

        await _dbContext.SaveChangesAsync(ct);
        return demotedCount;
    }
}
