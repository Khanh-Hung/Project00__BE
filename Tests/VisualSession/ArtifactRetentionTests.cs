using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class ArtifactRetentionTests
{
    private static (ProjectDbContext Db, ArtifactRetentionService Service) CreateContext()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var service = new ArtifactRetentionService(db, new SystemDateTimeProvider(), NullLogger<ArtifactRetentionService>.Instance);
        return (db, service);
    }

    [Fact]
    public async Task CurrentArtifact_IsIndefinitelyProtected()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var currentImg = new SceneImage(sessionId, charId, Guid.NewGuid(), 1, "https://cdn.project00.ai/current.png", "prompt", isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        await db.SceneImages.AddAsync(currentImg);
        await db.SaveChangesAsync();

        var eval = await service.EvaluateEligibilityAsync(currentImg.Id);

        Assert.True(eval.IsProtected);
        Assert.False(eval.IsEligibleForCleanup);
        Assert.Equal("CurrentArtifactProtected", eval.ProtectionReason);
    }

    [Fact]
    public async Task ActivePredecessor_IsProtectedFromCleanup()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var predecessorImg = new SceneImage(sessionId, charId, Guid.NewGuid(), 1, "https://cdn.project00.ai/predecessor.png", "prompt", isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Historical);
        await db.SceneImages.AddAsync(predecessorImg);

        var currentImg = new SceneImage(sessionId, charId, Guid.NewGuid(), 2, "https://cdn.project00.ai/current.png", "prompt", isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current, predecessorArtifactId: predecessorImg.Id);
        await db.SceneImages.AddAsync(currentImg);

        await db.SaveChangesAsync();

        var eval = await service.EvaluateEligibilityAsync(predecessorImg.Id);

        Assert.True(eval.IsProtected);
        Assert.False(eval.IsEligibleForCleanup);
        Assert.Equal("ActivePredecessorProtected", eval.ProtectionReason);
    }

    [Fact]
    public async Task InFlightJobArtifact_IsProtectedFromCleanup()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, Guid.NewGuid(), charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(job);

        var inFlightArtifact = new SceneImage(sessionId, charId, job.TurnId, 1, "https://cdn.project00.ai/in_flight.png", "prompt", generationJobId: job.Id, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Candidate);
        await db.SceneImages.AddAsync(inFlightArtifact);

        await db.SaveChangesAsync();

        var eval = await service.EvaluateEligibilityAsync(inFlightArtifact.Id);

        Assert.True(eval.IsProtected);
        Assert.False(eval.IsEligibleForCleanup);
        Assert.Equal("InFlightJobProtected", eval.ProtectionReason);
    }

    [Fact]
    public async Task CleanupExpiredArtifacts_MarksQuarantinedAndHistoricalPastTTL_AsDeleted()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var oldTime = DateTime.UtcNow.AddDays(-10);

        // Expired quarantined artifact (10 days old, TTL 7 days)
        var quarantinedImg = new SceneImage(sessionId, charId, Guid.NewGuid(), 1, "https://cdn.project00.ai/quarantined.png", "prompt", isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Quarantined);
        quarantinedImg.SetCreated(oldTime);
        await db.SceneImages.AddAsync(quarantinedImg);

        // Active current artifact
        var currentImg = new SceneImage(sessionId, charId, Guid.NewGuid(), 2, "https://cdn.project00.ai/current.png", "prompt", isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        currentImg.SetCreated(oldTime);
        await db.SceneImages.AddAsync(currentImg);

        await db.SaveChangesAsync();

        var cleanedCount = await service.CleanupExpiredArtifactsAsync(
            quarantinedTtl: TimeSpan.FromDays(7),
            orphanTtl: TimeSpan.FromDays(30));

        Assert.Equal(1, cleanedCount);

        var reloadedQuarantined = await db.SceneImages.FirstAsync(img => img.Id == quarantinedImg.Id);
        Assert.Equal(ArtifactLifecycleStatus.Deleted, reloadedQuarantined.LifecycleStatus);

        var reloadedCurrent = await db.SceneImages.FirstAsync(img => img.Id == currentImg.Id);
        Assert.Equal(ArtifactLifecycleStatus.Current, reloadedCurrent.LifecycleStatus);
        Assert.True(reloadedCurrent.IsCurrent);
    }
}
