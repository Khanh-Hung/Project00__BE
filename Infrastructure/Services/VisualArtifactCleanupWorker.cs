using Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Background worker that periodically evaluates and safely cleans up expired quarantined
/// and orphan visual artifacts without impacting the user request hot path.
/// </summary>
public sealed class VisualArtifactCleanupWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VisualArtifactCleanupWorker> _logger;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _quarantinedTtl;
    private readonly TimeSpan _orphanTtl;

    public VisualArtifactCleanupWorker(
        IServiceProvider serviceProvider,
        ILogger<VisualArtifactCleanupWorker> logger,
        TimeSpan? checkInterval = null,
        TimeSpan? quarantinedTtl = null,
        TimeSpan? orphanTtl = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _checkInterval = checkInterval ?? TimeSpan.FromHours(1);
        _quarantinedTtl = quarantinedTtl ?? TimeSpan.FromDays(7);
        _orphanTtl = orphanTtl ?? TimeSpan.FromDays(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[VisualArtifactCleanupWorker] Started background artifact retention cleaner.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformCleanupPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VisualArtifactCleanupWorker] Error occurred during artifact cleanup pass.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("[VisualArtifactCleanupWorker] Stopped background artifact retention cleaner.");
    }

    public async Task<int> PerformCleanupPassAsync(CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var retentionService = scope.ServiceProvider.GetRequiredService<IArtifactRetentionService>();

        var cleanedCount = await retentionService.CleanupExpiredArtifactsAsync(
            _quarantinedTtl,
            _orphanTtl,
            ct);

        if (cleanedCount > 0)
        {
            _logger.LogInformation("[VisualArtifactCleanupWorker] Cleanup pass finished. Cleaned {Count} expired artifacts.", cleanedCount);
        }

        return cleanedCount;
    }
}
