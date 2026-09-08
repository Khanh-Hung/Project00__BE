using Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure.Health;

public sealed class CoreDbContextHealthCheck : IHealthCheck
{
    private readonly CoreDbContext _dbContext;

    public CoreDbContextHealthCheck(CoreDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("Database connection successful.")
                : HealthCheckResult.Unhealthy("Database connection failed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database health check exception.", ex);
        }
 }
}
