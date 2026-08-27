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
    public async Task MissingArtifact_ReturnsNotEligibleAndNotProtected()
    {
        var (_, service) = CreateContext();
        var nonExistentId = Guid.NewGuid();

        var eval = await service.EvaluateEligibilityAsync(nonExistentId);

        Assert.False(eval.IsProtected);
        Assert.False(eval.IsEligibleForCleanup);
        Assert.Equal("ArtifactNotFound", eval.ProtectionReason);
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
    public async Task CleanupExpiredArtifacts_UsesQuarantinedAtTimestamp_NotJustCreatedAt()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var oldCreatedAt = DateTime.UtcNow.AddDays(-20);
        var recentQuarantinedAt = DateTime.UtcNow.AddDays(-2); // Quarantined 2 days ago (TTL: 7 days) -> NOT EXPIRED!
        var expiredQuarantinedAt = DateTime.UtcNow.AddDays(-10); // Quarantined 10 days ago (TTL: 7 days) -> EXPIRED!

        // Artifact 1: Old created, but recently quarantined (within 7-day TTL)
        var recentQuarantined = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: Guid.NewGuid(),
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/recent_q.png",
            prompt: "1girl",
            isCurrent: false,
            lifecycleStatus: ArtifactLifecycleStatus.Quarantined,
            quarantinedAt: recentQuarantinedAt
        );
        recentQuarantined.SetCreated(oldCreatedAt);
        await db.SceneImages.AddAsync(recentQuarantined);

        // Artifact 2: Quarantined 10 days ago (past 7-day TTL)
        var expiredQuarantined = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: Guid.NewGuid(),
            sceneRevision: 2,
            imageUrl: "https://cdn.project00.ai/expired_q.png",
            prompt: "1girl",
            isCurrent: false,
            lifecycleStatus: ArtifactLifecycleStatus.Quarantined,
            quarantinedAt: expiredQuarantinedAt
        );
        expiredQuarantined.SetCreated(oldCreatedAt);
        await db.SceneImages.AddAsync(expiredQuarantined);

        await db.SaveChangesAsync();

        var cleanedCount = await service.CleanupExpiredArtifactsAsync(
            quarantinedTtl: TimeSpan.FromDays(7),
            orphanTtl: TimeSpan.FromDays(30));

        // Exactly 1 artifact cleaned (expiredQuarantined), recentQuarantined is PRESERVED!
        Assert.Equal(1, cleanedCount);

        var reloadedRecent = await db.SceneImages.FirstAsync(img => img.Id == recentQuarantined.Id);
        Assert.Equal(ArtifactLifecycleStatus.Quarantined, reloadedRecent.LifecycleStatus);

        var reloadedExpired = await db.SceneImages.FirstAsync(img => img.Id == expiredQuarantined.Id);
        Assert.Equal(ArtifactLifecycleStatus.Deleted, reloadedExpired.LifecycleStatus);
    }
}
