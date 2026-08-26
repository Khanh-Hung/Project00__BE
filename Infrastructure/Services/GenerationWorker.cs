using Application.Common;
using Application.DTOs;
using Application.Enums;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Infrastructure.Services;

/// <summary>
/// Bounded background worker executing generation jobs from the queue.
/// Enforces GPU concurrency isolation (MaxConcurrentGenerations = 1 per worker),
/// handles failure classification, exponential backoff scheduling, and cancellation checks.
/// </summary>
public sealed class GenerationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGenerationJobQueue _jobQueue;
    private readonly ILogger<GenerationWorker> _logger;
    private readonly string _workerId;
    private readonly TimeSpan _leaseDuration = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gpuConcurrencyThrottle;

    public GenerationWorker(
        IServiceScopeFactory scopeFactory,
        IGenerationJobQueue jobQueue,
        ILogger<GenerationWorker> logger,
        string? workerId = null,
        int maxConcurrentGenerations = 1)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerId = workerId ?? $"worker-gen-{Guid.NewGuid():N}";
        _gpuConcurrencyThrottle = new SemaphoreSlim(Math.Max(1, maxConcurrentGenerations));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[GenerationWorkerStarted] WorkerId={WorkerId} listening on GenerationQueue.", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? jobId = null;
            try
            {
                jobId = await _jobQueue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GenerationWorkerDequeueError] Error dequeuing job.");
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            if (jobId.HasValue && jobId.Value != Guid.Empty)
            {
                await ProcessJobWithThrottleAsync(jobId.Value, stoppingToken);
            }
        }

        _logger.LogInformation("[GenerationWorkerStopped] WorkerId={WorkerId} stopped.", _workerId);
    }

    public async Task<JobExecutionResult> ProcessJobDirectAsync(Guid jobId, CancellationToken ct = default)
    {
        await _gpuConcurrencyThrottle.WaitAsync(ct);
        try
        {
            return await ExecuteJobAsync(jobId, ct);
        }
        finally
        {
            _gpuConcurrencyThrottle.Release();
        }
    }

    private async Task ProcessJobWithThrottleAsync(Guid jobId, CancellationToken ct)
    {
        await _gpuConcurrencyThrottle.WaitAsync(ct);
        try
        {
            await ExecuteJobAsync(jobId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[GenerationWorkerUnhandledError] Unhandled error processing JobId={JobId}.", jobId);
        }
        finally
        {
            _gpuConcurrencyThrottle.Release();
        }
    }

    private async Task<JobExecutionResult> ExecuteJobAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ProjectDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IImageGenerationOrchestrator>();
        var dateTimeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var retryPolicy = scope.ServiceProvider.GetService<GenerationRetryPolicy>() ?? GenerationRetryPolicy.Default;

        var now = dateTimeProvider.UtcNow;
        var job = await dbContext.ImageGenerationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job == null)
        {
            _logger.LogWarning("[GenerationWorkerJobNotFound] JobId={JobId} not found in DB. Skipping.", jobId);
            return new JobExecutionResult(JobExecutionStatus.Failed, "Job not found");
        }

        // 1. Check if job is already cancelled or terminal
        if (job.CancellationRequested || job.Status is ImageJobStatus.Cancelled or ImageJobStatus.Completed or ImageJobStatus.Quarantined)
        {
            _logger.LogInformation("[GenerationWorkerSkipped] JobId={JobId} is already in terminal/cancelled state {Status}. Skipping.", jobId, job.Status);
            return new JobExecutionResult(JobExecutionStatus.Skipped, "Job is already terminal or cancelled");
        }

        // 2. Claim Worker Lease
        bool claimed = false;
        if (dbContext.Database.IsRelational())
        {
            var rowsClaimed = await dbContext.ImageGenerationJobs
                .Where(j => j.Id == jobId && (j.Status == ImageJobStatus.Pending || j.Status == ImageJobStatus.Queued || (j.LeaseUntil.HasValue && j.LeaseUntil.Value <= now)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, ImageJobStatus.Processing)
                    .SetProperty(j => j.ClaimedBy, _workerId)
                    .SetProperty(j => j.LeaseUntil, now.Add(_leaseDuration))
                    .SetProperty(j => j.StartedAt, now)
                    .SetProperty(j => j.AttemptCount, j => j.AttemptCount + 1)
                    .SetProperty(j => j.CurrentAttemptNumber, j => j.AttemptCount + 1)
                    .SetProperty(j => j.Version, j => j.Version + 1)
                    .SetProperty(j => j.UpdatedAt, now), ct);

            claimed = rowsClaimed > 0;
            if (claimed)
            {
                await dbContext.Entry(job).ReloadAsync(ct);
            }
        }
        else
        {
            claimed = job.TryClaim(_workerId, _leaseDuration, now);
            if (claimed)
            {
                await dbContext.SaveChangesAsync(ct);
            }
        }

        if (!claimed)
        {
            _logger.LogInformation("[GenerationWorkerClaimFailed] Worker {WorkerId} lost claim race on JobId={JobId}. Skipping duplicate execution.", _workerId, jobId);
            return new JobExecutionResult(JobExecutionStatus.Deferred, "Failed to acquire execution lease");
        }

        _logger.LogInformation("[GenerationWorkerClaimed] Worker {WorkerId} claimed JobId={JobId} (Attempt #{Attempt}).", _workerId, jobId, job.AttemptCount);

        // 3. Construct Outbox Payload for Orchestration
        var snapshot = new VisualSnapshot(
            TurnId: job.TurnId,
            SessionId: job.SessionId,
            CharacterId: job.CharacterId,
            SceneRevision: job.SceneRevision,
            VisualIdentity: null,
            SceneState: new SessionSceneState("active scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: job.TurnId,
            CharacterId: job.CharacterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: job.GenerationRequestId
        );

        var sw = Stopwatch.StartNew();

        try
        {
            var result = await orchestrator.OrchestrateSceneImageGenerationAsync(payload, Guid.NewGuid(), _workerId, now, ct);
            sw.Stop();
            GenerationObservability.ExecutionDurationMs.Record(sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var category = ClassifyException(ex);
            _logger.LogError(ex, "[GenerationWorkerFailed] JobId={JobId} failed with category {Category}: {Message}", jobId, category, ex.Message);

            if (retryPolicy.ShouldRetry(category, job.RetryCount, out var delay))
            {
                var nextAttempt = dateTimeProvider.UtcNow.Add(delay);
                _logger.LogWarning("[GenerationWorkerRetryScheduled] Scheduling retry for JobId={JobId} at {NextAttempt:O} (Delay={Delay}s).", jobId, nextAttempt, delay.TotalSeconds);

                if (dbContext.Database.IsRelational())
                {
                    await dbContext.ImageGenerationJobs
                        .Where(j => j.Id == jobId && j.ClaimedBy == _workerId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, ImageJobStatus.Queued)
                            .SetProperty(j => j.RetryCount, j => j.RetryCount + 1)
                            .SetProperty(j => j.NextAttemptAt, nextAttempt)
                            .SetProperty(j => j.FailureReason, ex.Message)
                            .SetProperty(j => j.IsRetryable, true)
                            .SetProperty(j => j.ClaimedBy, (string?)null)
                            .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                            .SetProperty(j => j.Version, j => j.Version + 1)
                            .SetProperty(j => j.UpdatedAt, dateTimeProvider.UtcNow), CancellationToken.None);
                }
                else
                {
                    job.ScheduleRetry(nextAttempt, ex.Message, dateTimeProvider.UtcNow, _workerId);
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }

                GenerationObservability.RetriesTotal.Add(1);
                return new JobExecutionResult(JobExecutionStatus.Deferred, $"Retry scheduled: {ex.Message}");
            }
            else
            {
                // Terminal Failure
                if (dbContext.Database.IsRelational())
                {
                    await dbContext.ImageGenerationJobs
                        .Where(j => j.Id == jobId && j.ClaimedBy == _workerId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, ImageJobStatus.Failed)
                            .SetProperty(j => j.FailureReason, ex.Message)
                            .SetProperty(j => j.IsRetryable, false)
                            .SetProperty(j => j.CompletedAt, dateTimeProvider.UtcNow)
                            .SetProperty(j => j.ClaimedBy, (string?)null)
                            .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                            .SetProperty(j => j.Version, j => j.Version + 1)
                            .SetProperty(j => j.UpdatedAt, dateTimeProvider.UtcNow), CancellationToken.None);
                }
                else
                {
                    job.Fail(ex.Message, isRetryable: false, dateTimeProvider.UtcNow, _workerId);
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }

                GenerationObservability.JobsFailedTotal.Add(1);
                return new JobExecutionResult(JobExecutionStatus.Failed, ex.Message);
            }
        }
    }

    public static GenerationFailureCategory ClassifyException(Exception ex) => ex switch
    {
        OperationCanceledException => GenerationFailureCategory.Cancellation,
        GpuTransientException gpuEx when gpuEx.StatusCode == 408 => GenerationFailureCategory.ProviderTimeout,
        GpuTransientException gpuEx when gpuEx.StatusCode == 429 => GenerationFailureCategory.ProviderRateLimited,
        GpuTransientException gpuEx when gpuEx.StatusCode >= 500 => GenerationFailureCategory.ProviderUnavailable,
        GpuTransientException => GenerationFailureCategory.GpuFailure,
        GpuNonTransientException => GenerationFailureCategory.InvalidWorkflow,
        TimeoutException => GenerationFailureCategory.ProviderTimeout,
        HttpRequestException => GenerationFailureCategory.TransientNetwork,
        DbUpdateConcurrencyException => GenerationFailureCategory.DatabaseTransient,
        DbUpdateException => GenerationFailureCategory.DatabaseTransient,
        ArgumentException => GenerationFailureCategory.InvalidInput,
        _ => GenerationFailureCategory.Unknown
    };
}
