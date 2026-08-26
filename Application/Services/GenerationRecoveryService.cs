using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

/// <summary>
/// Authoritative lease crash-recovery service.
/// Detects abandoned worker leases, fences stale workers via CAS increments,
/// and safely requeues or terminates unrecovered jobs.
/// </summary>
public sealed class GenerationRecoveryService
{
    private readonly ProjectDbContext _dbContext;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<GenerationRecoveryService> _logger;
    private readonly GenerationRetryPolicy _retryPolicy;
    private readonly IGenerationJobQueue? _jobQueue;

    public GenerationRecoveryService(
        ProjectDbContext dbContext,
        IDateTimeProvider dateTimeProvider,
        ILogger<GenerationRecoveryService> logger,
        GenerationRetryPolicy? retryPolicy = null,
        IGenerationJobQueue? jobQueue = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _retryPolicy = retryPolicy ?? GenerationRetryPolicy.Default;
        _jobQueue = jobQueue;
    }

    /// <summary>
    /// Scans for and recovers all expired worker leases.
    /// Returns the number of successfully recovered / transitioned jobs.
    /// </summary>
    public async Task<int> RecoverExpiredJobsAsync(DateTime? referenceTime = null, CancellationToken ct = default)
    {
        var now = referenceTime ?? _dateTimeProvider.UtcNow;

        // Query jobs under active processing/evaluating status whose lease has expired without an accepted attempt
        var expiredJobs = await _dbContext.ImageGenerationJobs
            .Where(j => (j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating)
                        && j.LeaseUntil.HasValue
                        && j.LeaseUntil.Value <= now
                        && j.AcceptedAttemptId == null)
            .ToListAsync(ct);

        if (expiredJobs.Count == 0)
            return 0;

        _logger.LogWarning("[GenerationRecoveryScan] Found {Count} expired generation job leases at {Now:O}.", expiredJobs.Count, now);

        int recoveredCount = 0;

        foreach (var job in expiredJobs)
        {
            var expectedVersion = job.Version;

            if (job.CancellationRequested)
            {
                // Handle cancellation request that occurred while lease expired
                if (_dbContext.Database.IsRelational())
                {
                    var rows = await _dbContext.ImageGenerationJobs
                        .Where(j => j.Id == job.Id
                                    && j.Version == expectedVersion
                                    && (j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, ImageJobStatus.Cancelled)
                            .SetProperty(j => j.FailureReason, "Job cancelled while lease expired")
                            .SetProperty(j => j.CompletedAt, now)
                            .SetProperty(j => j.ClaimedBy, (string?)null)
                            .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                            .SetProperty(j => j.Version, j => j.Version + 1)
                            .SetProperty(j => j.UpdatedAt, now), ct);

                    if (rows > 0)
                    {
                        recoveredCount++;
                        _logger.LogInformation("[GenerationRecoveryCancelled] JobId={JobId} transitioned to Cancelled.", job.Id);
                    }
                }
                else
                {
                    try
                    {
                        job.Cancel(now);
                        await _dbContext.SaveChangesAsync(ct);
                        recoveredCount++;
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        _logger.LogWarning("[GenerationRecoveryRace] Concurrency conflict recovering JobId={JobId}. Skipping.", job.Id);
                    }
                }
            }
            else if (job.RetryCount < _retryPolicy.MaxRetries)
            {
                // Requeue eligible job
                if (_dbContext.Database.IsRelational())
                {
                    var rows = await _dbContext.ImageGenerationJobs
                        .Where(j => j.Id == job.Id
                                    && j.Version == expectedVersion
                                    && (j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, ImageJobStatus.Queued)
                            .SetProperty(j => j.RetryCount, j => j.RetryCount + 1)
                            .SetProperty(j => j.NextAttemptAt, now)
                            .SetProperty(j => j.FailureReason, "Lease expired: automatically recovered")
                            .SetProperty(j => j.IsRetryable, true)
                            .SetProperty(j => j.ClaimedBy, (string?)null)
                            .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                            .SetProperty(j => j.Version, j => j.Version + 1)
                            .SetProperty(j => j.UpdatedAt, now), ct);

                    if (rows > 0)
                    {
                        recoveredCount++;
                        _logger.LogInformation("[GenerationRecoveryRequeued] JobId={JobId} requeued to Queued (Attempt #{RetryCount}).", job.Id, job.RetryCount + 1);
                        if (_jobQueue != null)
                        {
                            await _jobQueue.EnqueueAsync(job.Id, priority: 1, ct);
                        }
                    }
                }
                else
                {
                    try
                    {
                        job.ResetToPending();
                        job.MarkQueued(now);
                        await _dbContext.SaveChangesAsync(ct);
                        recoveredCount++;
                        if (_jobQueue != null)
                        {
                            await _jobQueue.EnqueueAsync(job.Id, priority: 1, ct);
                        }
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        _logger.LogWarning("[GenerationRecoveryRace] Concurrency conflict recovering JobId={JobId}. Skipping.", job.Id);
                    }
                }
            }
            else
            {
                // Max retries exceeded -> Terminally fail the job
                if (_dbContext.Database.IsRelational())
                {
                    var rows = await _dbContext.ImageGenerationJobs
                        .Where(j => j.Id == job.Id
                                    && j.Version == expectedVersion
                                    && (j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, ImageJobStatus.Failed)
                            .SetProperty(j => j.FailureReason, $"Lease expired and max retries ({_retryPolicy.MaxRetries}) exhausted")
                            .SetProperty(j => j.IsRetryable, false)
                            .SetProperty(j => j.CompletedAt, now)
                            .SetProperty(j => j.ClaimedBy, (string?)null)
                            .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                            .SetProperty(j => j.Version, j => j.Version + 1)
                            .SetProperty(j => j.UpdatedAt, now), ct);

                    if (rows > 0)
                    {
                        recoveredCount++;
                        _logger.LogWarning("[GenerationRecoveryFailed] JobId={JobId} permanently failed: max retries exhausted.", job.Id);
                    }
                }
                else
                {
                    try
                    {
                        job.Fail($"Lease expired and max retries ({_retryPolicy.MaxRetries}) exhausted", isRetryable: false, now, job.ClaimedBy ?? "recovery-service");
                        await _dbContext.SaveChangesAsync(ct);
                        recoveredCount++;
                    }
                    catch (Exception ex) when (ex is DbUpdateConcurrencyException or InvalidOperationException)
                    {
                        _logger.LogWarning("[GenerationRecoveryRace] Concurrency conflict failing expired JobId={JobId}. Skipping.", job.Id);
                    }
                }
            }
        }

        return recoveredCount;
    }
}
