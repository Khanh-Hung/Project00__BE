using Application.DTOs;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
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

        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 100);
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

        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 100);
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

    [Fact]
    public async Task RecoverExpiredJobs_OnStartupOrRestart_RedispatchesPendingOutboxMessagesIntoQueue()
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

        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("active scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: snapshot.TurnId,
            CharacterId: snapshot.CharacterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: Guid.NewGuid()
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.OutboxMessages.AddAsync(outboxMessage);
            await dbSeed.SaveChangesAsync();
        }

        // Empty in-memory queue on process startup
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 100);
        Assert.Equal(0, queue.CurrentDepth);

        using (var dbRecovery = new ProjectDbContext(options))
        {
            var recoveryService = new GenerationRecoveryService(
                dbContext: dbRecovery,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationRecoveryService>.Instance,
                retryPolicy: GenerationRetryPolicy.Default,
                jobQueue: queue
            );

            // Recovery cycle re-hydrates the queue from durable DB outbox
            await recoveryService.RecoverExpiredJobsAsync();
        }

        Assert.Equal(1, queue.CurrentDepth);
        var item = await queue.DequeueAsync();
        Assert.NotNull(item);
        Assert.Equal(payload.GenerationRequestId, item.Payload.GenerationRequestId);
        Assert.Equal(outboxMessage.Id, item.OutboxId);
    }

    [Fact]
    public async Task RecoverExpiredJobs_AppliesExponentialBackoff_ToNextAttemptAt()
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
        var policy = GenerationRetryPolicy.Deterministic(maxRetries: 5, baseDelay: TimeSpan.FromSeconds(1));

        // Create 3 jobs with different RetryCounts whose worker lease expired
        var job0 = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job0.TryClaim("worker-1", TimeSpan.FromMinutes(2), now.AddMinutes(-5));

        var job1 = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job1.TryClaim("worker-2", TimeSpan.FromMinutes(2), now.AddMinutes(-5));
        job1.ScheduleRetry(now.AddMinutes(-5), "first failure", now.AddMinutes(-5));
        job1.TryClaim("worker-2", TimeSpan.FromMinutes(2), now.AddMinutes(-5)); // RetryCount = 1

        var job2 = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job2.TryClaim("worker-3", TimeSpan.FromMinutes(2), now.AddMinutes(-5));
        job2.ScheduleRetry(now.AddMinutes(-5), "first failure", now.AddMinutes(-5));
        job2.TryClaim("worker-3", TimeSpan.FromMinutes(2), now.AddMinutes(-5));
        job2.ScheduleRetry(now.AddMinutes(-5), "second failure", now.AddMinutes(-5));
        job2.TryClaim("worker-3", TimeSpan.FromMinutes(2), now.AddMinutes(-5)); // RetryCount = 2

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddRangeAsync(job0, job1, job2);
            await dbSeed.SaveChangesAsync();
        }

        using (var dbRecovery = new ProjectDbContext(options))
        {
            var recoveryService = new GenerationRecoveryService(
                dbContext: dbRecovery,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationRecoveryService>.Instance,
                retryPolicy: policy
            );

            var count = await recoveryService.RecoverExpiredJobsAsync(now);
            Assert.Equal(3, count);
        }

        using (var dbVerify = new ProjectDbContext(options))
        {
            var v0 = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job0.Id);
            var v1 = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job1.Id);
            var v2 = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job2.Id);

            // Backoff formula: baseDelay * 2^retryCount
            // Attempt 0 -> retry 1: delay = 1 * 2^0 = 1s -> NextAttemptAt = now + 1s
            // Attempt 1 -> retry 2: delay = 1 * 2^1 = 2s -> NextAttemptAt = now + 2s
            // Attempt 2 -> retry 3: delay = 1 * 2^2 = 4s -> NextAttemptAt = now + 4s
            Assert.NotNull(v0.NextAttemptAt);
            Assert.NotNull(v1.NextAttemptAt);
            Assert.NotNull(v2.NextAttemptAt);

            Assert.Equal(now.AddSeconds(1), v0.NextAttemptAt.Value);
            Assert.Equal(now.AddSeconds(2), v1.NextAttemptAt.Value);
            Assert.Equal(now.AddSeconds(4), v2.NextAttemptAt.Value);
        }
    }

    [Fact]
    public async Task RecoverExpiredJobs_AuthoritativeGate_CannotBypassNextAttemptAt()
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

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: characterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: reqId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );

        // Job has scheduled retry 30 seconds into the future
        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, reqId);
        job.ScheduleRetry(now.AddSeconds(30), "backoff in progress", now);

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.OutboxMessages.AddAsync(outboxMessage);
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 100);

        // 1. Scan at time 'now': NextAttemptAt is 30s in the future -> MUST NOT ENQUEUE
        using (var dbRecovery1 = new ProjectDbContext(options))
        {
            var recoveryService = new GenerationRecoveryService(
                dbContext: dbRecovery1,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationRecoveryService>.Instance,
                retryPolicy: GenerationRetryPolicy.Default,
                jobQueue: queue
            );

            await recoveryService.RecoverExpiredJobsAsync(now);
        }

        Assert.Equal(0, queue.CurrentDepth); // Backoff gate successfully protected!

        // 2. Scan at time 'now + 31s': NextAttemptAt has elapsed -> MUST ENQUEUE
        using (var dbRecovery2 = new ProjectDbContext(options))
        {
            var recoveryService = new GenerationRecoveryService(
                dbContext: dbRecovery2,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationRecoveryService>.Instance,
                retryPolicy: GenerationRetryPolicy.Default,
                jobQueue: queue
            );

            await recoveryService.RecoverExpiredJobsAsync(now.AddSeconds(31));
        }

        Assert.Equal(1, queue.CurrentDepth); // Successfully enqueued once due!
    }

    [Fact]
    public async Task Recovery_ConcurrentInstances_OnlyOneInstanceClaimsAndEnqueuesOutboxMessage()
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
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: snapshot.TurnId,
            CharacterId: snapshot.CharacterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: Guid.NewGuid()
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.OutboxMessages.AddAsync(outboxMessage);
            await dbSeed.SaveChangesAsync();
        }

        // Two independent in-memory queues (simulating Instance A and Instance B)
        using var queueA = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 100);
        using var queueB = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 100);

        using var dbA = new ProjectDbContext(options);
        using var dbB = new ProjectDbContext(options);

        var recoveryA = new GenerationRecoveryService(
            dbContext: dbA,
            dateTimeProvider: new SystemDateTimeProvider(),
            logger: NullLogger<GenerationRecoveryService>.Instance,
            retryPolicy: GenerationRetryPolicy.Default,
            jobQueue: queueA
        );

        var recoveryB = new GenerationRecoveryService(
            dbContext: dbB,
            dateTimeProvider: new SystemDateTimeProvider(),
            logger: NullLogger<GenerationRecoveryService>.Instance,
            retryPolicy: GenerationRetryPolicy.Default,
            jobQueue: queueB
        );

        // Run recovery concurrently on Instance A and Instance B
        await Task.WhenAll(
            recoveryA.RecoverExpiredJobsAsync(now),
            recoveryB.RecoverExpiredJobsAsync(now)
        );

        // Exactly ONE instance must win the claim and enqueue the message
        Assert.Equal(1, queueA.CurrentDepth + queueB.CurrentDepth);

        using var dbVerify = new ProjectDbContext(options);
        var finalOutbox = await dbVerify.OutboxMessages.FirstAsync(m => m.Id == outboxMessage.Id);
        Assert.Equal(OutboxStatus.Processing, finalOutbox.Status);
        Assert.Equal("recovery-dispatcher", finalOutbox.ClaimedBy);
    }

    [Fact]
    public async Task Recovery_CrashAfterClaim_ReclaimsStaleProcessing_AndDownstreamIdempotencyPreventsDuplicateExecution()
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

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: characterId,
            UserId: Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: reqId
        );

        // Simulate a crashed node that claimed the outbox message 3 minutes ago
        var staleOutbox = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        staleOutbox.MarkProcessing("crashed-node-1", now.AddMinutes(-3));

        // Simulate downstream Job already completed by a peer worker
        var completedJob = new ImageGenerationJob(sessionId, turnId, characterId, 1, reqId);
        completedJob.TryClaim("worker-peer", TimeSpan.FromMinutes(2), now.AddMinutes(-2));
        var attempt = new ImageGenerationAttempt(
            generationJobId: completedJob.Id,
            turnId: turnId,
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 123,
            parametersJson: "{}",
            generationFingerprint: "fp-done",
            status: GenerationAttemptStatus.Succeeded
        );

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.OutboxMessages.AddAsync(staleOutbox);
            await dbSeed.ImageGenerationJobs.AddAsync(completedJob);
            await dbSeed.ImageGenerationAttempts.AddAsync(attempt);
            await dbSeed.SaveChangesAsync();

            completedJob.AcceptAttempt(attempt.Id, now.AddMinutes(-1), "worker-peer");
            await dbSeed.SaveChangesAsync();
        }

        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 100);

        // Run recovery: detects stale outbox lease, reclaims to Pending, but respects terminal downstream Job gate
        using (var dbRecovery = new ProjectDbContext(options))
        {
            var recoveryService = new GenerationRecoveryService(
                dbContext: dbRecovery,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationRecoveryService>.Instance,
                retryPolicy: GenerationRetryPolicy.Default,
                jobQueue: queue
            );

            await recoveryService.RecoverExpiredJobsAsync(now);
        }

        using (var dbVerify = new ProjectDbContext(options))
        {
            var reclaimedOutbox = await dbVerify.OutboxMessages.FirstAsync(m => m.Id == staleOutbox.Id);
            // Outbox was safely reclaimed from stale processing
            Assert.Equal(OutboxStatus.Pending, reclaimedOutbox.Status);
            Assert.Null(reclaimedOutbox.ProcessingStartedAt);
            Assert.Null(reclaimedOutbox.ClaimedBy);
        }

        // Downstream gate verified completed job -> skipped redundant GPU queue dispatch!
        Assert.Equal(0, queue.CurrentDepth);
    }
}
