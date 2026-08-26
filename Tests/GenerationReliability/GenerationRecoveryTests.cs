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

public sealed class GenerationRecoveryTests
{
    [Fact]
    public async Task RecoverExpiredJobs_ProcessingJobWithExpiredLease_RequeuesJob()
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
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-A", TimeSpan.FromMinutes(2), now.AddMinutes(-5)); // Expired 3 minutes ago

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 100);
        var timeProvider = new SystemDateTimeProvider();

        using (var dbRecovery = new ProjectDbContext(options))
        {
            var recoveryService = new GenerationRecoveryService(
                dbContext: dbRecovery,
                dateTimeProvider: timeProvider,
                logger: NullLogger<GenerationRecoveryService>.Instance,
                retryPolicy: GenerationRetryPolicy.Deterministic(maxRetries: 3),
                jobQueue: queue
            );

            var recoveredCount = await recoveryService.RecoverExpiredJobsAsync(now);
            Assert.Equal(1, recoveredCount);
        }

        using (var dbVerify = new ProjectDbContext(options))
        {
            var verifiedJob = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Queued, verifiedJob.Status);
            Assert.Null(verifiedJob.ClaimedBy);
            Assert.Null(verifiedJob.LeaseUntil);
            Assert.Equal(1, queue.CurrentDepth);
        }
    }

    [Fact]
    public async Task RecoverExpiredJobs_ActiveLease_DoesNotRequeue()
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
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-A", TimeSpan.FromMinutes(5), now); // Active for 5 min

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 100);
        var timeProvider = new SystemDateTimeProvider();

        using (var dbRecovery = new ProjectDbContext(options))
        {
            var recoveryService = new GenerationRecoveryService(
                dbContext: dbRecovery,
                dateTimeProvider: timeProvider,
                logger: NullLogger<GenerationRecoveryService>.Instance,
                retryPolicy: GenerationRetryPolicy.Deterministic(maxRetries: 3),
                jobQueue: queue
            );

            var recoveredCount = await recoveryService.RecoverExpiredJobsAsync(now);
            Assert.Equal(0, recoveredCount);
        }

        using (var dbVerify = new ProjectDbContext(options))
        {
            var verifiedJob = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Processing, verifiedJob.Status);
            Assert.Equal("worker-A", verifiedJob.ClaimedBy);
            Assert.Equal(0, queue.CurrentDepth);
        }
    }

    [Fact]
    public async Task RecoverExpiredJobs_WhenCancellationRequested_TransitionsToCancelled()
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
        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-A", TimeSpan.FromMinutes(2), now.AddMinutes(-5));
        job.RequestCancellation(now);

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        using (var dbRecovery = new ProjectDbContext(options))
        {
            var recoveryService = new GenerationRecoveryService(
                dbContext: dbRecovery,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationRecoveryService>.Instance,
                retryPolicy: GenerationRetryPolicy.Deterministic(maxRetries: 3)
            );

            var recoveredCount = await recoveryService.RecoverExpiredJobsAsync(now);
            Assert.Equal(1, recoveredCount);
        }

        using (var dbVerify = new ProjectDbContext(options))
        {
            var verifiedJob = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Cancelled, verifiedJob.Status);
        }
    }
}
