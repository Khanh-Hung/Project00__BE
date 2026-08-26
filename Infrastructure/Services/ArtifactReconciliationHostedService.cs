using Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Hosted background service periodically reconciling orphan/unclaimed generation artifacts.
/// </summary>
public sealed class ArtifactReconciliationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ArtifactReconciliationHostedService> _logger;
    private readonly TimeSpan _scanInterval = TimeSpan.FromMinutes(1);

    public ArtifactReconciliationHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ArtifactReconciliationHostedService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ArtifactReconciliationHostedService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var reconciliationService = scope.ServiceProvider.GetRequiredService<IArtifactReconciliationService>();
                await reconciliationService.ReconcileOrphanArtifactsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during artifact reconciliation scan cycle.");
            }

            try
            {
                await Task.Delay(_scanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ArtifactReconciliationHostedService stopped.");
    }
}
