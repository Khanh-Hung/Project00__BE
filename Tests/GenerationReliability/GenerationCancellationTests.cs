using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.ImageGeneration.ComfyUI;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GenerationReliability;

public sealed class GenerationCancellationTests
{
    private sealed class TrackingComfyClient : IComfyUIClient
    {
        public bool DeleteQueuedPromptCalled { get; private set; }
        public string? DeletedPromptId { get; private set; }
        public bool InterruptCalled { get; private set; }

        public Task<string> QueuePromptAsync(Dictionary<string, object> promptGraph, CancellationToken ct = default) => Task.FromResult("prompt-1");
        public Task<ComfyUIHistoryResult?> GetHistoryAsync(string promptId, CancellationToken ct = default) => Task.FromResult<ComfyUIHistoryResult?>(null);
        public Task<byte[]> DownloadImageAsync(string filename, string? subfolder = null, string? type = "output", CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<bool> DeleteQueuedPromptAsync(string promptId, CancellationToken ct = default)
        {
            DeleteQueuedPromptCalled = true;
            DeletedPromptId = promptId;
            return Task.FromResult(true);
        }
        public Task InterruptAsync(CancellationToken ct = default)
        {
            InterruptCalled = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CancelQueuedJob_TransitionsToCancelledImmediately()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new CoreDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(DateTime.UtcNow);

        using (var dbSeed = new CoreDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        var mockComfy = new TrackingComfyClient();

        using (var dbCancel = new CoreDbContext(options))
        {
            var cancellationService = new GenerationCancellationService(
                dbContext: dbCancel,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationCancellationService>.Instance,
                comfyClient: mockComfy
            );

            var cancelled = await cancellationService.RequestCancellationAsync(job.Id, "User cancel");
            Assert.True(cancelled);
        }

        using (var dbVerify = new CoreDbContext(options))
        {
            var verifiedJob = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Cancelled, verifiedJob.Status);
            Assert.True(verifiedJob.CancellationRequested);
        }
    }

    [Fact]
    public async Task CancelProcessingJob_SetsCancellationRequested_AndCallsProviderInterrupt()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new CoreDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        using (var dbSeed = new CoreDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        var mockComfy = new TrackingComfyClient();

        using (var dbCancel = new CoreDbContext(options))
        {
            var cancellationService = new GenerationCancellationService(
                dbContext: dbCancel,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationCancellationService>.Instance,
                comfyClient: mockComfy
            );

            var cancelled = await cancellationService.RequestCancellationAsync(job.Id, "User cancel");
            Assert.True(cancelled);
            Assert.True(mockComfy.InterruptCalled);
        }

        using (var dbVerify = new CoreDbContext(options))
        {
            var verifiedJob = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.True(verifiedJob.CancellationRequested);
        }
    }

    [Fact]
    public async Task CancelCompletedJob_ReturnsFalse_AndDoesNotMutateState()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new CoreDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(
            generationJobId: job.Id,
            turnId: job.TurnId,
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 12345,
            parametersJson: "{}",
            generationFingerprint: "fp_cancel_test",
            status: GenerationAttemptStatus.Succeeded
        );

        using (var dbSeed = new CoreDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.ImageGenerationAttempts.AddAsync(attempt);
            await dbSeed.SaveChangesAsync();

            job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1");
            await dbSeed.SaveChangesAsync();
        }

        var mockComfy = new TrackingComfyClient();

        using (var dbCancel = new CoreDbContext(options))
        {
            var cancellationService = new GenerationCancellationService(
                dbContext: dbCancel,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationCancellationService>.Instance,
                comfyClient: mockComfy
            );

            var cancelled = await cancellationService.RequestCancellationAsync(job.Id, "User cancel");
            Assert.False(cancelled);
            Assert.False(mockComfy.InterruptCalled);
        }

        using (var dbVerify = new CoreDbContext(options))
        {
            var verifiedJob = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Completed, verifiedJob.Status);
        }
    }

    [Fact]
    public async Task CancelProcessingJob_WithProviderJobId_DeletesTargetedPromptFirst()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new CoreDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        job.SetProviderJobId("prompt-targeted-999");

        using (var dbSeed = new CoreDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        var mockComfy = new TrackingComfyClient();

        using (var dbCancel = new CoreDbContext(options))
        {
            var cancellationService = new GenerationCancellationService(
                dbContext: dbCancel,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationCancellationService>.Instance,
                comfyClient: mockComfy
            );

            var cancelled = await cancellationService.RequestCancellationAsync(job.Id, "User cancel");
            Assert.True(cancelled);
            Assert.True(mockComfy.DeleteQueuedPromptCalled);
            Assert.Equal("prompt-targeted-999", mockComfy.DeletedPromptId);
        }
    }

    [Fact]
    public async Task Cancellation_ConcurrentlyFencesArtifactAcceptance_AndPreventsArtifactPromotion()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new CoreDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var now = DateTime.UtcNow;
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var reqId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, reqId);
        job.TryClaim("worker-A", TimeSpan.FromMinutes(2), now);

        var attempt = new ImageGenerationAttempt(
            generationJobId: job.Id,
            turnId: turnId,
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 42,
            parametersJson: "{}",
            generationFingerprint: "fp-race-test",
            status: GenerationAttemptStatus.Evaluating,
            claimedBy: "worker-A",
            startedAt: now,
            leaseUntil: now.AddMinutes(2)
        );

        using (var dbSeed = new CoreDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.ImageGenerationAttempts.AddAsync(attempt);
            await dbSeed.SaveChangesAsync();
        }

        // 1. User / Worker B cancels the job concurrently
        using (var dbCancel = new CoreDbContext(options))
        {
            var cancellationService = new GenerationCancellationService(
                dbContext: dbCancel,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<GenerationCancellationService>.Instance
            );

            var cancelled = await cancellationService.RequestCancellationAsync(job.Id, "User cancelled during evaluation");
            Assert.True(cancelled);
        }

        // 2. Worker A finishes quality evaluation and attempts to atomically accept the artifact
        using (var dbAccept = new CoreDbContext(options))
        {
            var acceptanceService = new ArtifactAcceptanceService(
                dbContext: dbAccept,
                dateTimeProvider: new SystemDateTimeProvider(),
                logger: NullLogger<ArtifactAcceptanceService>.Instance
            );

            var snapshot = new Domain.ValueObjects.VisualSnapshot(
                TurnId: turnId,
                SessionId: sessionId,
                CharacterId: characterId,
                SceneRevision: 1,
                VisualIdentity: null,
                SceneState: new Domain.ValueObjects.SessionSceneState("scene", "neutral"),
                TransientState: null,
                GenerationProfile: Domain.ValueObjects.GenerationProfile.CreateDefault()
            );

            var acceptRequest = new ArtifactAcceptanceRequest(
                JobId: job.Id,
                WinningAttemptId: attempt.Id,
                Snapshot: snapshot,
                ImageUrl: "https://cdn.project00.ai/race_test.png",
                CompiledPrompt: "prompt",
                ResolvedPreviousSceneImageUrl: null,
                GenerationFingerprint: "fp-race-test",
                MetadataJson: "{}",
                IsIdentityPassed: true,
                WorkerId: "worker-A",
                OutboxId: Guid.NewGuid()
            );

            var acceptResult = await acceptanceService.AcceptAttemptAtomicallyAsync(acceptRequest);

            // Acceptance MUST fail/defer due to cancellation fence!
            Assert.Equal(JobExecutionStatus.Deferred, acceptResult.Status);
        }

        // 3. Authoritative verification: Job is NOT Completed, AcceptedAttemptId is null, and no SceneImage was promoted to IsCurrent
        using (var dbVerify = new CoreDbContext(options))
        {
            var finalJob = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.NotEqual(ImageJobStatus.Completed, finalJob.Status);
            Assert.Null(finalJob.AcceptedAttemptId);
            Assert.True(finalJob.CancellationRequested);

            var currentImages = await dbVerify.SceneImages
                .Where(img => img.SessionId == sessionId && img.IsCurrent)
                .ToListAsync();

            Assert.Empty(currentImages); // Zero artifacts promoted to IsCurrent!
        }
    }
}
