using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GenerationReliability;

public sealed class ArtifactReconciliationTests
{
    [Fact]
    public async Task ReconcileOrphanArtifacts_DemotesInvalidCurrentImages_OnFailedOrCancelledJobs()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new ProjectDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var reqId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, reqId);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        job.Fail("Worker crashed before acceptance", isRetryable: false, DateTime.UtcNow, "worker-1");

        var orphanImage = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/orphan.png",
            prompt: "orphan prompt",
            generationRequestId: reqId,
            generationJobId: job.Id,
            isCurrent: true
        );

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SceneImages.AddAsync(orphanImage);
            await dbSeed.SaveChangesAsync();
        }

        using (var dbReconcile = new ProjectDbContext(options))
        {
            var reconciliationService = new ArtifactReconciliationService(
                dbContext: dbReconcile,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<ArtifactReconciliationService>.Instance
            );

            var demotedCount = await reconciliationService.ReconcileOrphanArtifactsAsync();
            Assert.Equal(1, demotedCount);
        }

        using (var dbVerify = new ProjectDbContext(options))
        {
            var verifiedImage = await dbVerify.SceneImages.FirstAsync(img => img.Id == orphanImage.Id);
            Assert.False(verifiedImage.IsCurrent); // Strictly demoted to false!
        }
    }

    [Fact]
    public async Task ReconcileOrphanArtifacts_IsIdempotent_AndPreservesValidAcceptedImages()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new ProjectDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var reqId = Guid.NewGuid();
        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, reqId);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(
            generationJobId: job.Id,
            turnId: turnId,
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 12345,
            parametersJson: "{}",
            generationFingerprint: "fp_valid",
            status: GenerationAttemptStatus.Succeeded
        );

        var validImage = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/valid.png",
            prompt: "valid prompt",
            generationRequestId: reqId,
            generationJobId: job.Id,
            isCurrent: true
        );

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.ImageGenerationAttempts.AddAsync(attempt);
            await dbSeed.SaveChangesAsync();

            job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1");
            await dbSeed.SceneImages.AddAsync(validImage);
            await dbSeed.SaveChangesAsync();
        }

        using (var dbReconcile = new ProjectDbContext(options))
        {
            var reconciliationService = new ArtifactReconciliationService(
                dbContext: dbReconcile,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<ArtifactReconciliationService>.Instance
            );

            var count1 = await reconciliationService.ReconcileOrphanArtifactsAsync();
            var count2 = await reconciliationService.ReconcileOrphanArtifactsAsync(); // 2nd run

            Assert.Equal(0, count1);
            Assert.Equal(0, count2);
        }

        using (var dbVerify = new ProjectDbContext(options))
        {
            var verifiedImage = await dbVerify.SceneImages.FirstAsync(img => img.Id == validImage.Id);
            Assert.True(verifiedImage.IsCurrent); // Remains validly current
        }
    }

    [Fact]
    public async Task Acceptance_ConcurrentlyWithReconciliation_PreservesAcceptedCurrentArtifact()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new ProjectDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var now = DateTime.UtcNow;
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var reqId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, reqId);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), now);

        var attempt = new ImageGenerationAttempt(
            generationJobId: job.Id,
            turnId: turnId,
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 42,
            parametersJson: "{}",
            generationFingerprint: "fp_concurrent",
            status: GenerationAttemptStatus.Succeeded
        );

        var candidateImage = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/concurrent.png",
            prompt: "concurrent prompt",
            generationRequestId: reqId,
            generationJobId: job.Id,
            isCurrent: true
        );

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.ImageGenerationAttempts.AddAsync(attempt);
            await dbSeed.SceneImages.AddAsync(candidateImage);
            await dbSeed.SaveChangesAsync();
        }

        // Simulate Worker committing acceptance: job is accepted and completed
        using (var dbAccept = new ProjectDbContext(options))
        {
            var targetJob = await dbAccept.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            targetJob.AcceptAttempt(attempt.Id, now, "worker-1");
            await dbAccept.SaveChangesAsync();
        }

        // Reconciliation runs concurrently
        using (var dbReconcile = new ProjectDbContext(options))
        {
            var reconciliationService = new ArtifactReconciliationService(
                dbContext: dbReconcile,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<ArtifactReconciliationService>.Instance
            );

            var demotedCount = await reconciliationService.ReconcileOrphanArtifactsAsync();
            Assert.Equal(0, demotedCount); // Reconciliation must NOT demote accepted artifact
        }

        using (var dbVerify = new ProjectDbContext(options))
        {
            var verifiedImage = await dbVerify.SceneImages.FirstAsync(img => img.Id == candidateImage.Id);
            Assert.True(verifiedImage.IsCurrent); // IsCurrent remains TRUE
        }
    }
}
