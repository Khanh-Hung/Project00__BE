using Application.Common;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Infrastructure.Services;

/// <summary>
/// Bounded background worker executing generation work items from the queue.
/// Enforces GPU concurrency isolation (MaxConcurrentGenerations = 1 per worker),
/// invokes the authoritative orchestrator with canonical payloads, and handles failure classification.
/// </summary>
public sealed class GenerationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGenerationJobQueue _jobQueue;
    private readonly ILogger<GenerationWorker> _logger;
    private readonly string _workerId;
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
            GenerationWorkItem? item = null;
            try
            {
                item = await _jobQueue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GenerationWorkerDequeueError] Error dequeuing work item.");
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            if (item != null)
            {
                await ProcessItemWithThrottleAsync(item, stoppingToken);
            }
        }

        _logger.LogInformation("[GenerationWorkerStopped] WorkerId={WorkerId} stopped.", _workerId);
    }

    public async Task<JobExecutionResult> ProcessWorkItemDirectAsync(GenerationWorkItem item, CancellationToken ct = default)
    {
        await _gpuConcurrencyThrottle.WaitAsync(ct);
        try
        {
            return await ExecuteWorkItemAsync(item, ct);
        }
        finally
        {
            _gpuConcurrencyThrottle.Release();
        }
    }

    private async Task ProcessItemWithThrottleAsync(GenerationWorkItem item, CancellationToken ct)
    {
        await _gpuConcurrencyThrottle.WaitAsync(ct);
        try
        {
            await ExecuteWorkItemAsync(item, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[GenerationWorkerUnhandledError] Unhandled error processing GenerationRequestId={RequestId}.", item.Payload.GenerationRequestId);
        }
        finally
        {
            _gpuConcurrencyThrottle.Release();
        }
    }

    private async Task<JobExecutionResult> ExecuteWorkItemAsync(GenerationWorkItem item, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ProjectDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IImageGenerationOrchestrator>();
        var dateTimeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var retryPolicy = scope.ServiceProvider.GetService<GenerationRetryPolicy>() ?? GenerationRetryPolicy.Default;

        var now = dateTimeProvider.UtcNow;
        var sw = Stopwatch.StartNew();

        try
        {
            var result = await orchestrator.OrchestrateSceneImageGenerationAsync(
                payload: item.Payload,
                outboxId: item.OutboxId,
                workerId: _workerId,
                now: now,
                ct: ct
            );

            sw.Stop();
            GenerationObservability.ExecutionDurationMs.Record(sw.ElapsedMilliseconds);

            if (result.Status == JobExecutionStatus.Completed)
            {
                GenerationObservability.JobsCompletedTotal.Add(1);
            }
            else if (result.Status == JobExecutionStatus.Deferred)
            {
                GenerationObservability.RetriesTotal.Add(1);
            }

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var category = ClassifyException(ex);
            _logger.LogError(ex, "[GenerationWorkerFailed] RequestId={RequestId} failed with category {Category}: {Message}",
                item.Payload.GenerationRequestId, category, ex.Message);

            if (category == GenerationFailureCategory.Cancellation)
            {
                GenerationObservability.JobsCancelledTotal.Add(1);
                return new JobExecutionResult(JobExecutionStatus.Skipped, "Job was cancelled");
            }

            if (GenerationRetryPolicy.IsRetryable(category))
            {
                GenerationObservability.RetriesTotal.Add(1);
                return new JobExecutionResult(JobExecutionStatus.Deferred, $"Retryable failure: {ex.Message}");
            }
            else
            {
                GenerationObservability.JobsFailedTotal.Add(1);
                return new JobExecutionResult(JobExecutionStatus.Failed, ex.Message);
            }
        }
    }

    public static GenerationFailureCategory ClassifyException(Exception ex) => GenerationFailureClassifier.Classify(ex);
}
