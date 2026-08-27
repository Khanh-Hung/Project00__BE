using Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Hosted background service periodically executing lease crash-recovery scans.
/// </summary>
public sealed class GenerationRecoveryHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GenerationRecoveryHostedService> _logger;
    private readonly TimeSpan _scanInterval = TimeSpan.FromSeconds(10);

    public GenerationRecoveryHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<GenerationRecoveryHostedService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GenerationRecoveryHostedService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var recoveryService = scope.ServiceProvider.GetRequiredService<IGenerationRecoveryService>();
                await recoveryService.RecoverExpiredJobsAsync(null, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during generation recovery scan cycle.");
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

        _logger.LogInformation("GenerationRecoveryHostedService stopped.");
    }
}
