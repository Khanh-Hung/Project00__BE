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
        job.AcceptAttempt(attemptId, Clock.Now, workerId: "worker-1", metadataJson: "{\"model\":\"flux\"}");

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
        job.Quarantine(lastAttemptId, "Invariant degraded across 3 attempts", Clock.Now, workerId: "worker-1");

        Assert.Equal(ImageJobStatus.Quarantined, job.Status);
        Assert.Null(job.AcceptedAttemptId); // P0-2: AcceptedAttemptId is strictly NULL for Quarantined jobs!
        Assert.Equal(lastAttemptId, job.QuarantinedAttemptId);
        Assert.Equal("Invariant degraded across 3 attempts", job.FailureReason);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public void ImageGenerationJob_AcceptAttempt_EmptyGuid_ThrowsArgumentException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);

        Assert.Throws<ArgumentException>(() => job.AcceptAttempt(Guid.Empty, Clock.Now, workerId: "worker-1"));
    }

    [Fact]
    public void ImageGenerationJob_CannotComplete_WithNullOrEmptyAcceptedAttemptId()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);

        Assert.Throws<ArgumentException>(() => job.AcceptAttempt(Guid.Empty, Clock.Now, "worker-1"));
        Assert.Equal(ImageJobStatus.Processing, job.Status);
        Assert.Null(job.AcceptedAttemptId);
    }

    #region Strict Illegal State Machine Transitions (Reviewer P0 Invariants)

    [Fact]
    public void StateTransition_Completed_To_Running_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);
        job.AcceptAttempt(Guid.NewGuid(), Clock.Now, workerId: "worker-1");

        Assert.Equal(ImageJobStatus.Completed, job.Status);
        Assert.Throws<InvalidOperationException>(() => job.StartRunning("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
        Assert.False(job.TryClaim("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
    }

    [Fact]
    public void StateTransition_Completed_To_Evaluating_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);
        job.AcceptAttempt(Guid.NewGuid(), Clock.Now, workerId: "worker-1");

        Assert.Throws<InvalidOperationException>(() => job.MarkEvaluating(Clock.Now, workerId: "worker-1"));
    }

    [Fact]
    public void StateTransition_Quarantined_To_Running_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);
        job.Quarantine(Guid.NewGuid(), "degraded", Clock.Now, workerId: "worker-1");

        Assert.Equal(ImageJobStatus.Quarantined, job.Status);
        Assert.Throws<InvalidOperationException>(() => job.StartRunning("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
        Assert.False(job.TryClaim("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
    }

    [Fact]
    public void StateTransition_Failed_To_Evaluating_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), Clock.Now);
        job.Fail("crash", isRetryable: false, Clock.Now, workerId: "worker-1");

        Assert.Equal(ImageJobStatus.Failed, job.Status);
        Assert.Throws<InvalidOperationException>(() => job.MarkEvaluating(Clock.Now, workerId: "worker-1"));
    }

    [Fact]
    public void StateTransition_Cancelled_To_Running_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.Cancel(Clock.Now);

        Assert.Equal(ImageJobStatus.Cancelled, job.Status);
        Assert.Throws<InvalidOperationException>(() => job.StartRunning("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
        Assert.False(job.TryClaim("worker-2", TimeSpan.FromMinutes(2), Clock.Now));
    }

    [Fact]
    public void StateTransition_Pending_To_Evaluating_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        Assert.Equal(ImageJobStatus.Pending, job.Status);

        Assert.Throws<InvalidOperationException>(() => job.MarkEvaluating(Clock.Now, workerId: "worker-1"));
    }

    [Fact]
    public void StateTransition_Pending_To_AcceptAttempt_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        Assert.Equal(ImageJobStatus.Pending, job.Status);

        Assert.Throws<InvalidOperationException>(() => job.AcceptAttempt(Guid.NewGuid(), Clock.Now, workerId: "worker-1"));
    }

    [Fact]
    public void StateTransition_Queued_To_Evaluating_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(Clock.Now);
        Assert.Equal(ImageJobStatus.Queued, job.Status);

        Assert.Throws<InvalidOperationException>(() => job.MarkEvaluating(Clock.Now, workerId: "worker-1"));
    }

    [Fact]
    public void StateTransition_Queued_To_AcceptAttempt_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(Clock.Now);

        Assert.Throws<InvalidOperationException>(() => job.AcceptAttempt(Guid.NewGuid(), Clock.Now, workerId: "worker-1"));
    }

    [Fact]
    public void Job_ExpiredLease_CannotFail_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-A", TimeSpan.FromMinutes(2), Clock.Now.AddMinutes(-5)); // expired 3 min ago

        Assert.Throws<InvalidOperationException>(() => job.Fail("crash", isRetryable: false, now: Clock.Now, workerId: "worker-A"));
    }

    [Fact]
    public void Job_WorkerMismatch_CannotFail_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-A", TimeSpan.FromMinutes(2), Clock.Now);

        Assert.Throws<InvalidOperationException>(() => job.Fail("crash", isRetryable: false, now: Clock.Now, workerId: "worker-B"));
    }

    [Fact]
    public void Job_ExpiredLease_CannotAcceptAttempt_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-A", TimeSpan.FromMinutes(2), Clock.Now.AddMinutes(-5));

        Assert.Throws<InvalidOperationException>(() => job.AcceptAttempt(Guid.NewGuid(), Clock.Now, workerId: "worker-A"));
    }

    [Fact]
    public void Job_ExpiredLease_CannotQuarantine_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-A", TimeSpan.FromMinutes(2), Clock.Now.AddMinutes(-5));

        Assert.Throws<InvalidOperationException>(() => job.Quarantine(Guid.NewGuid(), "degraded", Clock.Now, workerId: "worker-A"));
    }

    [Fact]
    public void Job_ExpiredLease_CannotMarkEvaluating_ThrowsInvalidOperationException()
    {
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-A", TimeSpan.FromMinutes(2), Clock.Now.AddMinutes(-5));

        Assert.Throws<InvalidOperationException>(() => job.MarkEvaluating(Clock.Now, workerId: "worker-A"));
    }

    #endregion

    #region ImageGenerationAttempt State Machine Invariants

    [Fact]
    public void ImageGenerationAttempt_StateTransitions_EnforcesLifecycle()
    {
        var jobId = Guid.NewGuid();
        var attempt = new ImageGenerationAttempt(jobId, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_1");
        attempt.TryClaim("worker-1", Clock.Now, TimeSpan.FromMinutes(2));
        Assert.Equal(GenerationAttemptStatus.Running, attempt.Status);

        attempt.StartEvaluating("worker-1", Clock.Now);
        Assert.Equal(GenerationAttemptStatus.Evaluating, attempt.Status);

        attempt.MarkSucceeded("https://cdn.project00.ai/image.png", "comfy_123", 0.88f, 0.92f, Clock.Now, "worker-1", Clock.Now);
        Assert.Equal(GenerationAttemptStatus.Succeeded, attempt.Status);

        // Terminal attempt cannot start evaluating again
        Assert.Throws<InvalidOperationException>(() => attempt.StartEvaluating("worker-1", Clock.Now));
    }

    [Fact]
    public void ImageGenerationAttempt_StartEvaluating_WhenWorkerMismatch_ThrowsInvalidOperationException()
    {
        var jobId = Guid.NewGuid();
        var attempt = new ImageGenerationAttempt(jobId, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_1");
        attempt.TryClaim("worker-A", Clock.Now, TimeSpan.FromMinutes(2));

        Assert.Throws<InvalidOperationException>(() => attempt.StartEvaluating("worker-B", Clock.Now));
    }

    [Fact]
    public void ImageGenerationAttempt_MarkSucceeded_WhenWorkerMismatch_ThrowsInvalidOperationException()
    {
        var jobId = Guid.NewGuid();
        var attempt = new ImageGenerationAttempt(jobId, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_1");
        attempt.TryClaim("worker-A", Clock.Now, TimeSpan.FromMinutes(2));

        Assert.Throws<InvalidOperationException>(() => attempt.MarkSucceeded("https://cdn.project00.ai/image.png", null, 0.9f, 0.9f, Clock.Now, "worker-B", Clock.Now));
    }

    [Fact]
    public void Attempt_Pending_CannotStartEvaluating_MustThrow()
    {
        var jobId = Guid.NewGuid();
        var attempt = new ImageGenerationAttempt(jobId, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_1", status: GenerationAttemptStatus.Pending);
        Assert.Equal(GenerationAttemptStatus.Pending, attempt.Status);

        Assert.Throws<InvalidOperationException>(() => attempt.StartEvaluating("worker-A", Clock.Now));
    }

    [Fact]
    public void Attempt_ExpiredLease_CannotStartEvaluating()
    {
        var jobId = Guid.NewGuid();
        var attempt = new ImageGenerationAttempt(jobId, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_1");
        attempt.TryClaim("worker-A", Clock.Now.AddMinutes(-5), TimeSpan.FromMinutes(2)); // expired 3 min ago

        Assert.Throws<InvalidOperationException>(() => attempt.StartEvaluating("worker-A", Clock.Now));
    }

    [Fact]
    public void Attempt_ExpiredLease_CannotMarkSucceeded()
    {
        var jobId = Guid.NewGuid();
        var attempt = new ImageGenerationAttempt(jobId, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_1");
        attempt.TryClaim("worker-A", Clock.Now, TimeSpan.FromMinutes(2));
        attempt.StartEvaluating("worker-A", Clock.Now);

        // Advance time past lease expiration
        var expiredTime = Clock.Now.AddMinutes(5);
        Assert.Throws<InvalidOperationException>(() => attempt.MarkSucceeded("https://cdn.project00.ai/image.png", "job_1", 0.9f, 0.9f, expiredTime, "worker-A", expiredTime));
    }

    [Fact]
    public void Attempt_ExpiredLease_CannotMarkDegraded()
    {
        var jobId = Guid.NewGuid();
        var attempt = new ImageGenerationAttempt(jobId, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_1");
        attempt.TryClaim("worker-A", Clock.Now, TimeSpan.FromMinutes(2));
        attempt.StartEvaluating("worker-A", Clock.Now);

        var expiredTime = Clock.Now.AddMinutes(5);
        Assert.Throws<InvalidOperationException>(() => attempt.MarkDegraded("https://cdn.project00.ai/image.png", "job_1", 0.6f, 0.6f, expiredTime, "worker-A", expiredTime));
    }

    [Fact]
    public void Attempt_ExpiredLease_CannotMarkFailed()
    {
        var jobId = Guid.NewGuid();
        var attempt = new ImageGenerationAttempt(jobId, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_1");
        attempt.TryClaim("worker-A", Clock.Now, TimeSpan.FromMinutes(2));

        var expiredTime = Clock.Now.AddMinutes(5);
        Assert.Throws<InvalidOperationException>(() => attempt.MarkFailed("GPU crash", expiredTime, "worker-A", expiredTime));
    }

    [Fact]
    public void Attempt_ExpiredLease_CannotMarkQuarantined()
    {
        var jobId = Guid.NewGuid();
        var attempt = new ImageGenerationAttempt(jobId, Guid.NewGuid(), 1, 1, 1000L, "{}", "fp_1");
        attempt.TryClaim("worker-A", Clock.Now, TimeSpan.FromMinutes(2));
        attempt.StartEvaluating("worker-A", Clock.Now);

        var expiredTime = Clock.Now.AddMinutes(5);
        Assert.Throws<InvalidOperationException>(() => attempt.MarkQuarantined("https://cdn.project00.ai/image.png", "job_1", 0.5f, 0.5f, expiredTime, "worker-A", expiredTime));
    }

    #endregion
}
