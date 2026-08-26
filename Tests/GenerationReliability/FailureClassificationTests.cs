using Application.Common;
using Application.Exceptions;
using Domain.Entities;
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

        Assert.Equal(GenerationFailureCategory.ProviderTimeout, GenerationFailureClassifier.Classify(ex408));
        Assert.Equal(GenerationFailureCategory.ProviderRateLimited, GenerationFailureClassifier.Classify(ex429));
        Assert.Equal(GenerationFailureCategory.ProviderUnavailable, GenerationFailureClassifier.Classify(ex503));
        Assert.Equal(GenerationFailureCategory.GpuFailure, GenerationFailureClassifier.Classify(exGpu));
    }

    [Fact]
    public void ClassifyException_MapsPermanentDefectsCorrectly()
    {
        var exNonTransient = new GpuNonTransientException("Invalid node graph syntax", 400);
        var exArg = new ArgumentException("Invalid seed");

        Assert.Equal(GenerationFailureCategory.InvalidWorkflow, GenerationFailureClassifier.Classify(exNonTransient));
        Assert.Equal(GenerationFailureCategory.InvalidInput, GenerationFailureClassifier.Classify(exArg));
    }

    [Fact]
    public void ClassifyException_MapsOperationalExceptionsCorrectly()
    {
        var exCancel = new OperationCanceledException();
        var exDbConcurrency = new DbUpdateConcurrencyException("Conflict");

        Assert.Equal(GenerationFailureCategory.Cancellation, GenerationFailureClassifier.Classify(exCancel));
        Assert.Equal(GenerationFailureCategory.DatabaseTransient, GenerationFailureClassifier.Classify(exDbConcurrency));
    }

    [Fact]
    public void Attempt_MarkFailed_WithClassifiedCategory_PreservesCategoryAccurately()
    {
        var attempt = new ImageGenerationAttempt(
            generationJobId: Guid.NewGuid(),
            turnId: Guid.NewGuid(),
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 12345,
            parametersJson: "{}",
            generationFingerprint: "fp-failure-test",
            status: GenerationAttemptStatus.Running,
            claimedBy: "worker-1",
            startedAt: DateTime.UtcNow,
            leaseUntil: DateTime.UtcNow.AddMinutes(2)
        );

        var now = DateTime.UtcNow;
        var ex = new GpuTransientException("Gateway timeout 504", 504);
        var category = GenerationFailureClassifier.Classify(ex);

        attempt.MarkFailed(category, ex.Message, now, "worker-1", now);

        Assert.Equal(GenerationAttemptStatus.Failed, attempt.Status);
        Assert.Equal(GenerationFailureCategory.ProviderUnavailable, attempt.FailureCategory);
        Assert.Equal("Gateway timeout 504", attempt.ErrorMessage);
    }
}
