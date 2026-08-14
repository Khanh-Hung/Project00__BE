using System.Collections.Concurrent;
using System.Reflection;
using Application.Abstractions.Responses;
using FluentValidation;
using MediatR;

namespace Application.Behaviors;

public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> 
    where TRequest : IRequest<TResponse>
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> _failureMethods = new();
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!_validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);
        var validationResults = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var validationFailures = validationResults
            .SelectMany(r => r.Errors)
            .Where(e => e != null)
            .ToList();

        if (validationFailures.Count != 0)
        {
            var responseType = typeof(TResponse);
            var errorMessages = validationFailures.Select(f => f.ErrorMessage).ToArray();

            if (responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>))
            {
                var innerType = responseType.GetGenericArguments()[0];
                var resultType = typeof(Result<>).MakeGenericType(innerType);
                
                var failureMethod = _failureMethods.GetOrAdd(resultType, type => 
                    type.GetMethod(
                        nameof(Result<object>.Failure),
                        BindingFlags.Public | BindingFlags.Static,
                        binder: null,
                        types: [typeof(string[])],
                        modifiers: null)!);

                var result = failureMethod.Invoke(null, new object[] { errorMessages });
                return (TResponse)result!;
            }

            throw new InvalidOperationException(
                $"ValidationBehavior requires {typeof(TResponse).Name} to be Result<T>.");
        }

        return await next();
    }
}
