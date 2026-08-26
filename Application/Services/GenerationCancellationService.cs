using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.ImageGeneration.ComfyUI;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

/// <summary>
/// First-class generation cancellation coordinator.
/// Handles idempotent cancellation requests across Queued, Processing, and Evaluating phases,
/// propagating interrupt signals to GPU providers and preventing late artifact promotion.
/// </summary>
public sealed class GenerationCancellationService
{
    private readonly ProjectDbContext _dbContext;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<GenerationCancellationService> _logger;
    private readonly IComfyUIClient? _comfyClient;

    public GenerationCancellationService(
        ProjectDbContext dbContext,
        IDateTimeProvider dateTimeProvider,
        ILogger<GenerationCancellationService> logger,
        IComfyUIClient? comfyClient = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _comfyClient = comfyClient;
    }

    /// <summary>
    /// Requests cancellation of a generation job.
    /// Returns true if cancellation was successfully recorded or applied; false if job is already terminal.
    /// </summary>
    public async Task<bool> RequestCancellationAsync(Guid jobId, string reason = "User cancelled generation", CancellationToken ct = default)
    {
        var now = _dateTimeProvider.UtcNow;
        var job = await _dbContext.ImageGenerationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job == null)
        {
            _logger.LogWarning("[GenerationCancelNotFound] JobId={JobId} not found.", jobId);
            return false;
        }

        // Terminal jobs cannot be cancelled
        if (job.Status is ImageJobStatus.Completed or ImageJobStatus.Quarantined or ImageJobStatus.Cancelled or ImageJobStatus.Failed)
        {
            _logger.LogInformation("[GenerationCancelIgnored] JobId={JobId} is already in terminal state {Status}.", jobId, job.Status);
            return false;
        }

        var expectedVersion = job.Version;

        if (job.Status is ImageJobStatus.Pending or ImageJobStatus.Queued)
        {
            // Immediate transition to Cancelled
            if (_dbContext.Database.IsRelational())
            {
                var rows = await _dbContext.ImageGenerationJobs
                    .Where(j => j.Id == jobId && j.Version == expectedVersion && (j.Status == ImageJobStatus.Pending || j.Status == ImageJobStatus.Queued))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, ImageJobStatus.Cancelled)
                        .SetProperty(j => j.CancellationRequested, true)
                        .SetProperty(j => j.FailureReason, reason)
                        .SetProperty(j => j.CompletedAt, now)
                        .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                        .SetProperty(j => j.Version, j => j.Version + 1)
                        .SetProperty(j => j.UpdatedAt, now), ct);

                if (rows > 0)
                {
                    _logger.LogInformation("[GenerationCancelledQueued] Queued JobId={JobId} cancelled immediately.", jobId);
                    return true;
                }
                return false;
            }
            else
            {
                job.RequestCancellation(now);
                await _dbContext.SaveChangesAsync(ct);
                _logger.LogInformation("[GenerationCancelledQueued] Queued JobId={JobId} cancelled immediately.", jobId);
                return true;
            }
        }
        else
        {
            // Active Processing / Evaluating: flag CancellationRequested and trigger provider interrupt
            if (_dbContext.Database.IsRelational())
            {
                var rows = await _dbContext.ImageGenerationJobs
                    .Where(j => j.Id == jobId && j.Version == expectedVersion)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.CancellationRequested, true)
                        .SetProperty(j => j.Version, j => j.Version + 1)
                        .SetProperty(j => j.UpdatedAt, now), ct);

                if (rows > 0)
                {
                    _logger.LogInformation("[GenerationCancellationFlagged] JobId={JobId} flagged for cancellation.", jobId);
                    await TryInterruptProviderAsync(ct);
                    return true;
                }
                return false;
            }
            else
            {
                job.RequestCancellation(now);
                await _dbContext.SaveChangesAsync(ct);
                _logger.LogInformation("[GenerationCancellationFlagged] JobId={JobId} flagged for cancellation.", jobId);
                await TryInterruptProviderAsync(ct);
                return true;
            }
        }
    }

    private async Task TryInterruptProviderAsync(CancellationToken ct)
    {
        if (_comfyClient != null)
        {
            try
            {
                await _comfyClient.InterruptAsync(ct);
                _logger.LogInformation("[GenerationProviderInterrupted] Sent interrupt signal to ComfyUI provider.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GenerationProviderInterruptFailed] Failed to signal interrupt to ComfyUI provider.");
            }
        }
    }
}
