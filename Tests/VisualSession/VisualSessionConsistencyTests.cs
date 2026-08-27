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
    private static ProjectDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProjectDbContext(options);
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
    public async Task ValidateConsistency_ForeignSessionArtifact_ReturnsCorrupted()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        // Artifact belongs to session B
        var foreignArtifact = new SceneImage(sessionB, charId, turnId, 1, "https://cdn.project00.ai/foreign.png", "prompt", visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        // State for session A points to foreign artifact from session B
        var sessionState = new VisualSessionState(sessionA, foreignArtifact.Id, Guid.NewGuid(), visualRevision: 1);

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

        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        var sessionState = new VisualSessionState(sessionId, artifact.Id, Guid.NewGuid(), visualRevision: 2); // Mismatch: 2 vs 1

        db.SceneImages.Add(artifact);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Corrupted, result.Status);
        Assert.Contains("VisualRevision mismatch", result.Reason);
    }

    [Fact]
    public async Task ValidateConsistency_QuarantinedArtifactAsCurrent_ReturnsCorrupted()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Quarantined);
        var sessionState = new VisualSessionState(sessionId, artifact.Id, Guid.NewGuid(), visualRevision: 1);

        db.SceneImages.Add(artifact);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Corrupted, result.Status);
        Assert.Contains("Quarantined", result.Reason);
    }

    [Fact]
    public async Task ValidateConsistency_DeletedArtifactAsCurrent_ReturnsInconsistent()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Deleted);
        var sessionState = new VisualSessionState(sessionId, artifact.Id, Guid.NewGuid(), visualRevision: 1);

        db.SceneImages.Add(artifact);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        var result = await service.ValidateConsistencyAsync(sessionId);

        Assert.Equal(VisualStateConsistencyStatus.Inconsistent, result.Status);
        Assert.Contains("Deleted", result.Reason);
    }
}
