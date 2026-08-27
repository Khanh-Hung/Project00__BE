using Application.Exceptions;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace Application.Common;

/// <summary>
/// Authoritative classifier mapping infrastructure exceptions to high-level GenerationFailureCategory.
/// Decouples domain logic from provider-specific HTTP and driver exceptions.
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
        DbUpdateConcurrencyException => GenerationFailureCategory.DatabaseTransient,
        DbUpdateException => GenerationFailureCategory.DatabaseTransient,
        DbException => GenerationFailureCategory.DatabaseTransient,
        ArgumentException => GenerationFailureCategory.InvalidInput,
        _ => GenerationFailureCategory.Unknown
    };
}
