using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.IdentityQualityGuard;

public sealed class GenerationStateMachineTests
{
    [Fact]
    public void ImageGenerationJob_InitialState_IsPending()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        Assert.Equal(ImageJobStatus.Pending, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(0, job.CurrentAttemptNumber);
        Assert.Null(job.AcceptedAttemptId);
    }

    [Fact]
    public void ImageGenerationJob_MarkQueued_FromPending_Succeeds()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(Clock.Now);
        Assert.Equal(ImageJobStatus.Queued, job.Status);
    }

    [Fact]
    public void ImageGenerationJob_AcceptAttempt_FromProcessingOrEvaluating_SetsCompletedAndAcceptedAttemptId()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);

        var attemptId = Guid.NewGuid();
        job.AcceptAttempt(attemptId, Clock.Now, metadataJson: "{\"model\":\"flux\"}");

        Assert.Equal(ImageJobStatus.Completed, job.Status);
        Assert.Equal(attemptId, job.AcceptedAttemptId);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.LeaseUntil);
    }

    [Fact]
    public void ImageGenerationJob_Quarantine_FromProcessingOrEvaluating_TransitionsToQuarantined()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);

        var lastAttemptId = Guid.NewGuid();
        job.Quarantine(lastAttemptId, "Invariant degraded across 3 attempts", Clock.Now);

        Assert.Equal(ImageJobStatus.Quarantined, job.Status);
        Assert.Equal(lastAttemptId, job.AcceptedAttemptId);
        Assert.Equal("Invariant degraded across 3 attempts", job.FailureReason);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public void ImageGenerationJob_AcceptAttempt_EmptyGuid_ThrowsArgumentException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);

        Assert.Throws<ArgumentException>(() => job.AcceptAttempt(Guid.Empty, Clock.Now));
    }

    #region Strict Illegal State Machine Transitions (Reviewer P0 Invariants)

    [Fact]
    public void StateTransition_Completed_To_Running_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);
        job.AcceptAttempt(Guid.NewGuid(), Clock.Now);

        Assert.Equal(ImageJobStatus.Completed, job.Status);
        Assert.Throws<InvalidOperationException>(() => job.StartRunning("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
        Assert.False(job.TryClaim("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
    }

    [Fact]
    public void StateTransition_Completed_To_Evaluating_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);
        job.AcceptAttempt(Guid.NewGuid(), Clock.Now);

        Assert.Throws<InvalidOperationException>(() => job.MarkEvaluating(Clock.Now));
    }

    [Fact]
    public void StateTransition_Quarantined_To_Running_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);
        job.Quarantine(Guid.NewGuid(), "degraded", Clock.Now);

        Assert.Equal(ImageJobStatus.Quarantined, job.Status);
        Assert.Throws<InvalidOperationException>(() => job.StartRunning("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
        Assert.False(job.TryClaim("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
    }

    [Fact]
    public void StateTransition_Failed_To_Evaluating_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);
        job.MarkFailed("crash", isRetryable: false, Clock.Now);

        Assert.Equal(ImageJobStatus.Failed, job.Status);
        Assert.Throws<InvalidOperationException>(() => job.MarkEvaluating(Clock.Now));
    }

    [Fact]
    public void StateTransition_Cancelled_To_Running_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkCancelled(Clock.Now);

        Assert.Equal(ImageJobStatus.Cancelled, job.Status);
        Assert.Throws<InvalidOperationException>(() => job.StartRunning("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
        Assert.False(job.TryClaim("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
    }

    [Fact]
    public void StateTransition_Pending_To_Evaluating_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        Assert.Equal(ImageJobStatus.Pending, job.Status);

        Assert.Throws<InvalidOperationException>(() => job.MarkEvaluating(Clock.Now));
    }

    [Fact]
    public void StateTransition_Pending_To_AcceptAttempt_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        Assert.Equal(ImageJobStatus.Pending, job.Status);

        Assert.Throws<InvalidOperationException>(() => job.AcceptAttempt(Guid.NewGuid(), Clock.Now));
    }

    [Fact]
    public void StateTransition_Queued_To_Evaluating_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(Clock.Now);
        Assert.Equal(ImageJobStatus.Queued, job.Status);

        Assert.Throws<InvalidOperationException>(() => job.MarkEvaluating(Clock.Now));
    }

    [Fact]
    public void StateTransition_Queued_To_AcceptAttempt_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(Clock.Now);

        Assert.Throws<InvalidOperationException>(() => job.AcceptAttempt(Guid.NewGuid(), Clock.Now));
    }

    #endregion

    #region ImageGenerationAttempt State Machine Invariants

    [Fact]
    public void ImageGenerationAttempt_StateTransitions_EnforcesLifecycle()
    {
        var jobId = Guid.NewGuid();
        var attempt = new ImageGenerationAttempt(jobId, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_1");
        Assert.Equal(GenerationAttemptStatus.Running, attempt.Status);

        attempt.StartEvaluating(Clock.Now);
        Assert.Equal(GenerationAttemptStatus.Evaluating, attempt.Status);

        attempt.MarkSucceeded("https://cdn.project00.ai/image.png", "comfy_123", 0.88f, 0.92f, Clock.Now);
        Assert.Equal(GenerationAttemptStatus.Succeeded, attempt.Status);

        // Terminal attempt cannot start evaluating again
        Assert.Throws<InvalidOperationException>(() => attempt.StartEvaluating(Clock.Now));
    }

    #endregion
}
