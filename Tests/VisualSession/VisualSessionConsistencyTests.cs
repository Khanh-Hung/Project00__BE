using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class VisualSessionConsistencyTests
{
    private static CoreDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CoreDbContext(options);
    }

    [Fact]
    public async Task ValidateConsistency_EmptySession_ReturnsHealthy()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Healthy, result.Status);
        Assert.Null(result.CurrentArtifactId);
    }

    [Fact]
    public async Task ValidateConsistency_ValidSessionWithMatchingLineage_ReturnsHealthy()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-valid", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", generationJobId: job.Id, generationAttemptId: attempt.Id, visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        attempt.AttachAcceptedArtifact(artifact.Id, DateTime.UtcNow);
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        var sessionState = new VisualSessionState(sessionId, artifact.Id, job.Id, visualRevision: 1);

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        db.SceneImages.Add(artifact);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Healthy, result.Status);
        Assert.Equal(artifact.Id, result.CurrentArtifactId);
        Assert.Equal(artifact.Id, result.ExpectedArtifactId);
    }

    [Fact]
    public async Task ValidateConsistency_MissingSessionState_WhenAuthoritativeAttemptExists_ReturnsRepairable()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-missing-state", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", generationJobId: job.Id, generationAttemptId: attempt.Id, visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        attempt.AttachAcceptedArtifact(artifact.Id, DateTime.UtcNow);
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        db.SceneImages.Add(artifact);
        // Note: VisualSessionStates is intentionally NOT added
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Repairable, result.Status);
        Assert.Null(result.CurrentArtifactId);
        Assert.Equal(artifact.Id, result.ExpectedArtifactId);
    }

    [Fact]
    public async Task ValidateConsistency_SessionStateNullPointer_WhenAuthoritativeAttemptExists_ReturnsRepairable()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-null-ptr", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", generationJobId: job.Id, generationAttemptId: attempt.Id, visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        attempt.AttachAcceptedArtifact(artifact.Id, DateTime.UtcNow);
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        var sessionState = new VisualSessionState(sessionId, currentImageId: null, currentGenerationJobId: job.Id, visualRevision: 1);

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        db.SceneImages.Add(artifact);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Repairable, result.Status);
        Assert.Null(result.CurrentArtifactId);
        Assert.Equal(artifact.Id, result.ExpectedArtifactId);
    }

    [Fact]
    public async Task ValidateConsistency_LineageFork_ArtifactGenerationAttemptIdMismatch_ReturnsCorrupted()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attemptA = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-attempt-a", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var attemptB = new ImageGenerationAttempt(job.Id, turnId, 1, 2, 2000L, "{}", "fp-attempt-b", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");

        // Artifact X has GenerationAttemptId = Attempt B, but Attempt A has AcceptedArtifactId = Artifact X
        var artifactX = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art_x.png", "prompt", generationJobId: job.Id, generationAttemptId: attemptB.Id, visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        attemptA.AttachAcceptedArtifact(artifactX.Id, DateTime.UtcNow);
        job.AcceptAttempt(attemptA.Id, DateTime.UtcNow, "worker-1", "{}");

        var sessionState = new VisualSessionState(sessionId, artifactX.Id, job.Id, visualRevision: 1);

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.AddRange(attemptA, attemptB);
        db.SceneImages.Add(artifactX);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Corrupted, result.Status);
        Assert.Contains("Lineage fork", result.Reason);
    }

    [Fact]
    public async Task ValidateConsistency_CompletedJobWithNullAcceptedAttemptId_ReturnsCorrupted()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        // Force Completed status without accepted attempt
        typeof(ImageGenerationJob).GetProperty(nameof(ImageGenerationJob.Status))!.SetValue(job, ImageJobStatus.Completed);

        var sessionState = new VisualSessionState(sessionId, Guid.NewGuid(), job.Id, visualRevision: 1);

        db.ImageGenerationJobs.Add(job);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Corrupted, result.Status);
        Assert.Contains("no recorded AcceptedAttemptId", result.Reason);
    }

    [Fact]
    public async Task ValidateConsistency_WinningAttemptNotSucceeded_ReturnsCorrupted()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-degraded", GenerationAttemptStatus.Degraded, claimedBy: "worker-1");
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        var sessionState = new VisualSessionState(sessionId, Guid.NewGuid(), job.Id, visualRevision: 1);

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Corrupted, result.Status);
        Assert.Contains("non-succeeded status", result.Reason);
    }

    [Fact]
    public async Task ValidateConsistency_ForeignSessionArtifact_ReturnsCorrupted()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionA, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        // Artifact belongs to foreign session B
        var foreignArtifact = new SceneImage(sessionB, charId, turnId, 1, "https://cdn.project00.ai/foreign.png", "prompt", generationJobId: job.Id, visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-foreign", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        attempt.AttachAcceptedArtifact(foreignArtifact.Id, DateTime.UtcNow);
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        var sessionState = new VisualSessionState(sessionA, foreignArtifact.Id, job.Id, visualRevision: 1);

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        db.SceneImages.Add(foreignArtifact);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionA);

        Assert.Equal(VisualStateConsistencyStatus.Corrupted, result.Status);
        Assert.Contains("foreign session", result.Reason);
    }

    [Fact]
    public async Task ValidateConsistency_RevisionMismatch_ReturnsCorrupted()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-rev-mismatch", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", generationJobId: job.Id, generationAttemptId: attempt.Id, visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        attempt.AttachAcceptedArtifact(artifact.Id, DateTime.UtcNow);
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        var sessionState = new VisualSessionState(sessionId, artifact.Id, job.Id, visualRevision: 2); // Mismatch: 2 vs 1

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        db.SceneImages.Add(artifact);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Corrupted, result.Status);
        Assert.Contains("VisualRevision mismatch", result.Reason);
    }

    [Fact]
    public async Task ValidateConsistency_QuarantinedArtifact_ReturnsCorrupted()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-quarantined", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", generationJobId: job.Id, generationAttemptId: attempt.Id, visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Quarantined);

        attempt.AttachAcceptedArtifact(artifact.Id, DateTime.UtcNow);
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        var sessionState = new VisualSessionState(sessionId, artifact.Id, job.Id, visualRevision: 1);

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        db.SceneImages.Add(artifact);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Corrupted, result.Status);
        Assert.Contains("Quarantined", result.Reason);
    }

    [Fact]
    public async Task ValidateConsistency_DeletedArtifact_ReturnsCorrupted()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-deleted", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", generationJobId: job.Id, generationAttemptId: attempt.Id, visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Deleted);

        attempt.AttachAcceptedArtifact(artifact.Id, DateTime.UtcNow);
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        var sessionState = new VisualSessionState(sessionId, artifact.Id, job.Id, visualRevision: 1);

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        db.SceneImages.Add(artifact);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Corrupted, result.Status);
        Assert.Contains("Deleted", result.Reason);
    }
}
