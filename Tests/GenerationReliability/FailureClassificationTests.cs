using Application.Exceptions;
using Domain.Enums;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tests.GenerationReliability;

public sealed class FailureClassificationTests
{
    [Fact]
    public void ClassifyException_MapsGpuTransientExceptionsCorrectly()
    {
        var ex408 = new GpuTransientException("Timeout", 408);
        var ex429 = new GpuTransientException("Rate limited", 429);
        var ex503 = new GpuTransientException("Unavailable", 503);
        var exGpu = new GpuTransientException("Out of memory", statusCode: null);

        Assert.Equal(GenerationFailureCategory.ProviderTimeout, GenerationWorker.ClassifyException(ex408));
        Assert.Equal(GenerationFailureCategory.ProviderRateLimited, GenerationWorker.ClassifyException(ex429));
        Assert.Equal(GenerationFailureCategory.ProviderUnavailable, GenerationWorker.ClassifyException(ex503));
        Assert.Equal(GenerationFailureCategory.GpuFailure, GenerationWorker.ClassifyException(exGpu));
    }

    [Fact]
    public void ClassifyException_MapsPermanentDefectsCorrectly()
    {
        var exNonTransient = new GpuNonTransientException("Invalid node graph syntax", 400);
        var exArg = new ArgumentException("Invalid seed");

        Assert.Equal(GenerationFailureCategory.InvalidWorkflow, GenerationWorker.ClassifyException(exNonTransient));
        Assert.Equal(GenerationFailureCategory.InvalidInput, GenerationWorker.ClassifyException(exArg));
    }

    [Fact]
    public void ClassifyException_MapsOperationalExceptionsCorrectly()
    {
        var exCancel = new OperationCanceledException();
        var exDbConcurrency = new DbUpdateConcurrencyException("Conflict");

        Assert.Equal(GenerationFailureCategory.Cancellation, GenerationWorker.ClassifyException(exCancel));
        Assert.Equal(GenerationFailureCategory.DatabaseTransient, GenerationWorker.ClassifyException(exDbConcurrency));
    }
}
