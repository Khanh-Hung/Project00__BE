using System.Collections.Concurrent;
using System.Reflection;
using Application.Abstractions.Responses;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Application.Behaviors;

public class ExceptionHandlingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> _failureMethods = new();
    private readonly ILogger<ExceptionHandlingBehavior<TRequest, TResponse>> _logger;

    public ExceptionHandlingBehavior(ILogger<ExceptionHandlingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception for {Request} {@Request}", typeof(TRequest).Name, request);

            var responseType = typeof(TResponse);

            if (responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>))
            {
                var valueType = responseType.GetGenericArguments()[0];
                var resultType = typeof(Result<>).MakeGenericType(valueType);

                var failureMethod = _failureMethods.GetOrAdd(resultType, type =>
                    type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .First(m => m.Name == "Failure" &&
                                    m.GetParameters().Length == 2 &&
                                    m.GetParameters()[0].ParameterType == typeof(int) &&
                                    m.GetParameters()[1].ParameterType == typeof(string[])));

                var result = failureMethod.Invoke(null, new object[]
                {
                    StatusCodes.Status500InternalServerError,
                    new string[] { "Internal server error" }
                });
                return (TResponse)result!;
            }

            throw;
        }
    }
}
