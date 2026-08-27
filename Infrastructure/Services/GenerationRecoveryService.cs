using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Infrastructure.Services;

/// <summary>
/// Authoritative lease crash-recovery and durable re-dispatch service.
/// Detects abandoned worker leases, fences stale workers via CAS increments,
/// applies exponential backoff with jitter to retries, and re-dispatches due pending/queued outbox jobs
/// into the in-memory queue using distributed atomic DB claims.
/// </summary>
public sealed class GenerationRecoveryService : IGenerationRecoveryService
{
    private readonly ProjectDbContext _dbContext;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<GenerationRecoveryService> _logger;
    private readonly GenerationRetryPolicy _retryPolicy;
    private readonly IGenerationJobQueue? _jobQueue;
    private readonly TimeSpan _outboxStaleLeaseTimeout = TimeSpan.FromMinutes(2);

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
    /// Scans for and recovers all expired worker leases, and re-dispatches due pending/queued jobs.
    /// Returns the number of successfully recovered / transitioned jobs.
    /// </summary>
    public async Task<int> RecoverExpiredJobsAsync(DateTime? referenceTime = null, CancellationToken ct = default)
    {
        var now = referenceTime ?? _dateTimeProvider.UtcNow;

        // 1. Reclaim abandoned outbox processing leases (crashed recovery/worker nodes)
        var staleCutoff = now - _outboxStaleLeaseTimeout;
        if (_dbContext.Database.IsRelational())
        {
            await _dbContext.OutboxMessages
                .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration
                            && m.Status == OutboxStatus.Processing
                            && m.ProcessingStartedAt != null
                            && m.ProcessingStartedAt <= staleCutoff)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxStatus.Pending)
                    .SetProperty(m => m.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(m => m.ClaimedBy, (string?)null)
                    .SetProperty(m => m.UpdatedAt, now), ct);
        }
        else
        {
            var staleOutbox = await _dbContext.OutboxMessages
                .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration
                            && m.Status == OutboxStatus.Processing
                            && m.ProcessingStartedAt != null
                            && m.ProcessingStartedAt <= staleCutoff)
                .ToListAsync(ct);

            foreach (var s in staleOutbox)
            {
                s.ReclaimStaleProcessing(now);
            }
            if (staleOutbox.Count > 0)
            {
                await _dbContext.SaveChangesAsync(ct);
            }
        }

        // 2. Query jobs under active processing/evaluating status whose lease has expired without an accepted attempt
        var expiredJobs = await _dbContext.ImageGenerationJobs
            .Where(j => (j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating)
                        && j.LeaseUntil.HasValue
                        && j.LeaseUntil.Value <= now
                        && j.AcceptedAttemptId == null)
            .ToListAsync(ct);

        int recoveredCount = 0;

        if (expiredJobs.Count > 0)
        {
            _logger.LogWarning("[GenerationRecoveryScan] Found {Count} expired generation job leases at {Now:O}.", expiredJobs.Count, now);

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
                    // Calculate exponential backoff delay with jitter
                    var backoffDelay = _retryPolicy.CalculateDelay(job.RetryCount);
                    var nextAttemptAt = now.Add(backoffDelay);

                    // Requeue eligible job with scheduled next attempt time
                    if (_dbContext.Database.IsRelational())
                    {
                        var rows = await _dbContext.ImageGenerationJobs
                            .Where(j => j.Id == job.Id
                                        && j.Version == expectedVersion
                                        && (j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating))
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(j => j.Status, ImageJobStatus.Queued)
                                .SetProperty(j => j.RetryCount, j => j.RetryCount + 1)
                                .SetProperty(j => j.NextAttemptAt, nextAttemptAt)
                                .SetProperty(j => j.FailureReason, $"Lease expired: automatically recovered with {backoffDelay.TotalSeconds:F1}s backoff")
                                .SetProperty(j => j.IsRetryable, true)
                                .SetProperty(j => j.ClaimedBy, (string?)null)
                                .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                                .SetProperty(j => j.Version, j => j.Version + 1)
                                .SetProperty(j => j.UpdatedAt, now), ct);

                        if (rows > 0)
                        {
                            recoveredCount++;
                            _logger.LogInformation("[GenerationRecoveryRequeued] JobId={JobId} requeued to Queued (Attempt #{RetryCount}) with NextAttemptAt={NextAttemptAt:O}.",
                                job.Id, job.RetryCount + 1, nextAttemptAt);
                        }
                    }
                    else
                    {
                        try
                        {
                            job.ScheduleRetry(nextAttemptAt, $"Lease expired: automatically recovered with {backoffDelay.TotalSeconds:F1}s backoff", now);
                            await _dbContext.SaveChangesAsync(ct);
                            recoveredCount++;
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

            if (recoveredCount > 0)
            {
                GenerationObservability.RecoveriesTotal.Add(recoveredCount);
            }
        }

        // 3. Durable Re-dispatch: Re-hydrate in-memory queue from pending outbox messages in DB, respecting ImageGenerationJob.NextAttemptAt authoritative schedule
        if (_jobQueue != null)
        {
            var dueOutboxMessages = await _dbContext.OutboxMessages
                .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration
                            && m.Status == OutboxStatus.Pending
                            && (m.NextRetryAt == null || m.NextRetryAt <= now))
                .OrderBy(m => m.CreatedAt)
                .Take(20)
                .ToListAsync(ct);

            foreach (var outboxMsg in dueOutboxMessages)
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMsg.PayloadJson);
                    if (payload?.Snapshot != null)
                    {
                        // Check authoritative ImageGenerationJob gate to prevent bypassing exponential backoff
                        var job = await _dbContext.ImageGenerationJobs
                            .AsNoTracking()
                            .FirstOrDefaultAsync(j => j.SessionId == payload.Snapshot.SessionId && j.GenerationRequestId == payload.GenerationRequestId, ct);

                        if (job != null)
                        {
                            // If terminal, skip
                            if (job.Status is ImageJobStatus.Completed or ImageJobStatus.Quarantined or ImageJobStatus.Cancelled or ImageJobStatus.Failed)
                            {
                                continue;
                            }

                            // If actively executing under valid lease, skip
                            if ((job.Status == ImageJobStatus.Processing || job.Status == ImageJobStatus.Evaluating)
                                && job.LeaseUntil.HasValue && job.LeaseUntil.Value > now)
                            {
                                continue;
                            }

                            // Authoritative Gate: If NextAttemptAt is in the future, backoff has not elapsed yet -> DO NOT ENQUEUE
                            if (job.NextAttemptAt.HasValue && job.NextAttemptAt.Value > now)
                            {
                                _logger.LogDebug("[GenerationRecoveryBackoffGate] JobId={JobId} is scheduled for retry at {NextAttemptAt:O} (now: {Now:O}). Respecting backoff.",
                                    job.Id, job.NextAttemptAt.Value, now);
                                continue;
                            }
                        }

                        // Distributed Atomic DB Claim: Transition Outbox Pending -> Processing
                        bool isClaimed = false;
                        if (_dbContext.Database.IsRelational())
                        {
                            var rowsClaimed = await _dbContext.OutboxMessages
                                .Where(m => m.Id == outboxMsg.Id && m.Status == OutboxStatus.Pending)
                                .ExecuteUpdateAsync(s => s
                                    .SetProperty(m => m.Status, OutboxStatus.Processing)
                                    .SetProperty(m => m.ProcessingStartedAt, now)
                                    .SetProperty(m => m.ClaimedBy, "recovery-dispatcher")
                                    .SetProperty(m => m.UpdatedAt, now), ct);

                            isClaimed = (rowsClaimed == 1);
                        }
                        else
                        {
                            if (outboxMsg.Status == OutboxStatus.Pending)
                            {
                                outboxMsg.MarkProcessing("recovery-dispatcher", now);
                                try
                                {
                                    await _dbContext.SaveChangesAsync(ct);
                                    isClaimed = true;
                                }
                                catch (DbUpdateConcurrencyException)
                                {
                                    isClaimed = false;
                                }
                            }
                        }

                        if (!isClaimed)
                        {
                            _logger.LogInformation("[GenerationRecoveryDispatchSkipped] OutboxMessage {Id} already claimed by concurrent instance. Skipping.", outboxMsg.Id);
                            continue;
                        }

                        try
                        {
                            var workItem = new GenerationWorkItem(payload, outboxMsg.Id, outboxMsg.CreatedAt, Priority: 5);
                            await _jobQueue.EnqueueAsync(workItem, ct);
                        }
                        catch (InvalidOperationException ex)
                        {
                            // Queue full (backpressure) -> Revert claim so other cycles can re-dispatch
                            if (_dbContext.Database.IsRelational())
                            {
                                await _dbContext.OutboxMessages
                                    .Where(m => m.Id == outboxMsg.Id && m.Status == OutboxStatus.Processing && m.ClaimedBy == "recovery-dispatcher")
                                    .ExecuteUpdateAsync(s => s
                                        .SetProperty(m => m.Status, OutboxStatus.Pending)
                                        .SetProperty(m => m.ProcessingStartedAt, (DateTime?)null)
                                        .SetProperty(m => m.ClaimedBy, (string?)null)
                                        .SetProperty(m => m.UpdatedAt, now), ct);
                            }
                            else
                            {
                                outboxMsg.MarkDeferred(now);
                                await _dbContext.SaveChangesAsync(ct);
                            }

                            _logger.LogWarning(ex, "[GenerationRecoveryQueueFull] Queue backpressure reached while re-dispatching OutboxId={OutboxId}. Reverted to Pending.", outboxMsg.Id);
                            break; // Queue full, leave remaining in DB for next scan cycle
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[GenerationRecoveryDispatchError] Error re-dispatching OutboxId={OutboxId}.", outboxMsg.Id);
                }
            }
        }

        return recoveredCount;
    }
}
