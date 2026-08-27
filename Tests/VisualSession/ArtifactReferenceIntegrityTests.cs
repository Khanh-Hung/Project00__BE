using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.VisualSession;

public sealed class ArtifactReferenceIntegrityTests
{
    [Fact]
    public void SucceededAttempt_AllowsAttachingAcceptedArtifact()
    {
        var jobId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var attempt = new ImageGenerationAttempt(jobId, turnId, 1, 1, 1000L, "{}", "fp-success", GenerationAttemptStatus.Running, claimedBy: "worker-1", startedAt: now, leaseUntil: now.AddMinutes(5));
        attempt.StartEvaluating("worker-1", now);
        attempt.MarkSucceeded("https://cdn.project00.ai/art.png", "pjob-1", 0.95f, 0.90f, now, "worker-1", now);

        Assert.Equal(GenerationAttemptStatus.Succeeded, attempt.Status);
        Assert.Null(attempt.AcceptedArtifactId);

        // Act
        attempt.AttachAcceptedArtifact(artifactId, now);

        // Assert
        Assert.Equal(artifactId, attempt.AcceptedArtifactId);
    }

    [Theory]
    [InlineData(GenerationAttemptStatus.Running)]
    [InlineData(GenerationAttemptStatus.Evaluating)]
    [InlineData(GenerationAttemptStatus.Degraded)]
    [InlineData(GenerationAttemptStatus.Quarantined)]
    [InlineData(GenerationAttemptStatus.Failed)]
    public void NonSucceededAttempt_RejectsAttachingAcceptedArtifact(GenerationAttemptStatus status)
    {
        var jobId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var attempt = new ImageGenerationAttempt(jobId, turnId, 1, 1, 1000L, "{}", $"fp-{status}", status, claimedBy: "worker-1", startedAt: now, leaseUntil: now.AddMinutes(5));

        var ex = Assert.Throws<InvalidOperationException>(() => attempt.AttachAcceptedArtifact(artifactId, now));
        Assert.Contains("must be Succeeded", ex.Message);
        Assert.Null(attempt.AcceptedArtifactId);
    }

    [Fact]
    public void AttachingArtifactTwice_ThrowsInvalidOperationException()
    {
        var jobId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var artifact1 = Guid.NewGuid();
        var artifact2 = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var attempt = new ImageGenerationAttempt(jobId, turnId, 1, 1, 1000L, "{}", "fp-dup-attach", GenerationAttemptStatus.Running, claimedBy: "worker-1", startedAt: now, leaseUntil: now.AddMinutes(5));
        attempt.StartEvaluating("worker-1", now);
        attempt.MarkSucceeded("https://cdn.project00.ai/art.png", "pjob-1", 0.95f, 0.90f, now, "worker-1", now);

        attempt.AttachAcceptedArtifact(artifact1, now);

        var ex = Assert.Throws<InvalidOperationException>(() => attempt.AttachAcceptedArtifact(artifact2, now));
        Assert.Contains("already has AcceptedArtifactId", ex.Message);
        Assert.Equal(artifact1, attempt.AcceptedArtifactId);
    }

    [Fact]
    public void AttachingEmptyArtifactId_ThrowsArgumentException()
    {
        var jobId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var attempt = new ImageGenerationAttempt(jobId, turnId, 1, 1, 1000L, "{}", "fp-empty-id", GenerationAttemptStatus.Running, claimedBy: "worker-1", startedAt: now, leaseUntil: now.AddMinutes(5));
        attempt.StartEvaluating("worker-1", now);
        attempt.MarkSucceeded("https://cdn.project00.ai/art.png", "pjob-1", 0.95f, 0.90f, now, "worker-1", now);

        Assert.Throws<ArgumentException>(() => attempt.AttachAcceptedArtifact(Guid.Empty, now));
        Assert.Null(attempt.AcceptedArtifactId);
    }
}
