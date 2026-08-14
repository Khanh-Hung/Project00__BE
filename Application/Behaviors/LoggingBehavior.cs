using Domain.Common.DateTimes;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Behaviors;

public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        _logger.LogInformation("START Handling {RequestName} || Request : {@Request}", requestName, request);

        var startTime = Clock.Now;

        try
        {
            var response = await next();

            var duration = (Clock.Now - startTime).TotalMilliseconds;

            if (duration > 500)
            {
                _logger.LogWarning("END (SLOW) Request: {RequestName} took {Duration}ms", requestName, duration);
            }
            else
            {
                _logger.LogInformation("END Handling {RequestName} in {Duration}ms", requestName, duration);
            }
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {RequestName}", requestName);
            throw;
        }
    }
}
