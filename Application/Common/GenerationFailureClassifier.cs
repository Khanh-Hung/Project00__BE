using Application.Exceptions;
using Domain.Enums;

namespace Application.Common;

/// <summary>
/// Authoritative classifier mapping application and provider exceptions to high-level GenerationFailureCategory.
/// Decoupled from concrete database/driver frameworks (Onion architecture compliant).
/// </summary>
public static class GenerationFailureClassifier
{
    public static GenerationFailureCategory Classify(Exception ex) => ex switch
    {
        OperationCanceledException => GenerationFailureCategory.Cancellation,
        GpuTransientException gpuEx when gpuEx.StatusCode == 408 => GenerationFailureCategory.ProviderTimeout,
        GpuTransientException gpuEx when gpuEx.StatusCode == 429 => GenerationFailureCategory.ProviderRateLimited,
        GpuTransientException gpuEx when gpuEx.StatusCode >= 500 => GenerationFailureCategory.ProviderUnavailable,
        GpuTransientException => GenerationFailureCategory.GpuFailure,
        GpuNonTransientException => GenerationFailureCategory.InvalidWorkflow,
        TimeoutException => GenerationFailureCategory.ProviderTimeout,
        HttpRequestException => GenerationFailureCategory.TransientNetwork,
        ArgumentException => GenerationFailureCategory.InvalidInput,
        _ => GenerationFailureCategory.Unknown
    };
}
