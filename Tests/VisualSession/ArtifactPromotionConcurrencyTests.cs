using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class ArtifactPromotionConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public ArtifactPromotionConcurrencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task ConcurrentWorkers_AcceptingSameJob_ExactlyOneWinsCASAndPromotesArtifact()
    {
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: charId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("throne room", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 42L)
        );

        var job = new ImageGenerationJob(
            sessionId: sessionId,
            turnId: turnId,
            characterId: charId,
            sceneRevision: 1,
            generationRequestId: Guid.NewGuid()
        );
        job.TryClaim("worker-winner", TimeSpan.FromMinutes(5), now);

        const int workerCount = 10;
        var attempts = Enumerable.Range(0, workerCount).Select(i =>
        {
            var workerId = i == 0 ? "worker-winner" : $"worker-{i}";
            var attempt = new ImageGenerationAttempt(
                generationJobId: job.Id,
                turnId: turnId,
                sceneRevision: 1,
                attemptNumber: i + 1,
                derivedSeed: 1000L + i,
                parametersJson: "{}",
                generationFingerprint: $"fp-conc-{i}",
                status: GenerationAttemptStatus.Running,
                claimedBy: workerId,
                startedAt: now,
                leaseUntil: now.AddMinutes(5)
            );
            attempt.StartEvaluating(workerId, now);
            attempt.MarkSucceeded($"https://cdn.project00.ai/conc_{i}.png", $"pjob-{i}", 0.95f, 0.90f, now, workerId, now);
            return attempt;
        }).ToList();

        await using (var seedDb = new CoreDbContext(_options))
        {
            await seedDb.ImageGenerationJobs.AddAsync(job);
            await seedDb.ImageGenerationAttempts.AddRangeAsync(attempts);
            await seedDb.SaveChangesAsync();
        }

        var dateTimeProvider = new SystemDateTimeProvider();

        // 10 concurrent worker tasks executing AcceptAttemptAtomicallyAsync
        var tasks = attempts.Select(async attempt =>
        {
            await using var taskDb = new CoreDbContext(_options);
            var service = new ArtifactAcceptanceService(taskDb, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);
            var request = new ArtifactAcceptanceRequest(
                JobId: job.Id,
                WinningAttemptId: attempt.Id,
                Snapshot: snapshot,
                ImageUrl: attempt.ImageUrl!,
                CompiledPrompt: "1girl in throne room",
                ResolvedPreviousSceneImageUrl: null,
                GenerationFingerprint: attempt.GenerationFingerprint,
                MetadataJson: "{}",
                IsIdentityPassed: true,
                WorkerId: attempt.ClaimedBy!,
                OutboxId: Guid.NewGuid(),
                Provenance: null
            );

            return await service.AcceptAttemptAtomicallyAsync(request, CancellationToken.None);
        });

        var results = await Task.WhenAll(tasks);

        // Assert: Exactly 1 Completed and 9 Deferred
        var successes = results.Where(r => r.Status == JobExecutionStatus.Completed).ToList();
        var deferreds = results.Where(r => r.Status == JobExecutionStatus.Deferred).ToList();

        Assert.Single(successes);
        Assert.Equal(workerCount - 1, deferreds.Count);

        // Verify relational state via a fresh DbContext
        await using (var verifyDb = new CoreDbContext(_options))
        {
            var finalJob = await verifyDb.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Completed, finalJob.Status);
            Assert.NotNull(finalJob.AcceptedAttemptId);

            var winningAttempt = await verifyDb.ImageGenerationAttempts.FirstAsync(a => a.Id == finalJob.AcceptedAttemptId.Value);
            Assert.NotNull(winningAttempt.AcceptedArtifactId);

            var currentArtifacts = await verifyDb.SceneImages.Where(img => img.SessionId == sessionId && img.IsCurrent).ToListAsync();
            Assert.Single(currentArtifacts);
            Assert.Equal(winningAttempt.AcceptedArtifactId.Value, currentArtifacts[0].Id);

            var sessionState = await verifyDb.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
            Assert.Equal(currentArtifacts[0].Id, sessionState.CurrentImageId);
            Assert.Equal(1, sessionState.VisualRevision);
        }
    }
}
