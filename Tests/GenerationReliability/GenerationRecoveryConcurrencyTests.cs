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

public sealed class GenerationRecoveryConcurrencyTests
{
    [Fact]
    public async Task StaleWorker_AfterRecovery_IsFencedByCASAndCannotMutateJob()
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
        job.TryClaim("worker-A", TimeSpan.FromMinutes(2), now.AddMinutes(-5)); // Expired lease, Version = 2

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        var staleSnapshotVersion = 2u;

        // 1. Recovery Scanner runs: Recovers Job to Queued (Version becomes 3)
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

        // 2. Worker B claims the job (Version becomes 4)
        using (var dbWorkerB = new ProjectDbContext(options))
        {
            var jobForB = await dbWorkerB.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Queued, jobForB.Status);
            // Claim before NextAttemptAt arrives is rejected
            var earlyClaimB = jobForB.TryClaim("worker-B", TimeSpan.FromMinutes(2), now);
            Assert.False(earlyClaimB);

            // Claim after NextAttemptAt backoff arrives succeeds
            var claimedB = jobForB.TryClaim("worker-B", TimeSpan.FromMinutes(2), now.AddSeconds(2));
            Assert.True(claimedB);
            await dbWorkerB.SaveChangesAsync();
        }

        // 3. Stale Worker A wakes up and attempts a relational CAS mutation with stale snapshot Version = 2
        using (var dbStaleWorkerA = new ProjectDbContext(options))
        {
            var rowsStaleA = await dbStaleWorkerA.ImageGenerationJobs
                .Where(j => j.Id == job.Id
                            && j.ClaimedBy == "worker-A"
                            && j.Version == staleSnapshotVersion)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, ImageJobStatus.Completed)
                    .SetProperty(j => j.CompletedAt, now));

            Assert.Equal(0, rowsStaleA); // Stale worker strictly fenced: 0 rows modified!
        }

        // 4. Verification: Job remains claimed and owned by Worker B
        using (var dbVerify = new ProjectDbContext(options))
        {
            var finalJob = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Processing, finalJob.Status);
            Assert.Equal("worker-B", finalJob.ClaimedBy);
            Assert.Null(finalJob.AcceptedAttemptId);
        }
    }
}
