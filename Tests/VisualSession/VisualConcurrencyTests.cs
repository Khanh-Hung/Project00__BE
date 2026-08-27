using Application.DTOs;
using Application.Enums;
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

public sealed class VisualConcurrencyTests
{
    private static VisualSnapshot CreateTestSnapshot(Guid sessionId, Guid turnId, Guid characterId, int sceneRevision = 1)
    {
        return new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: sceneRevision,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 1000L)
        );
    }

    [Fact]
    public async Task ScenarioA_TrueConcurrentAcceptances_RelationalDbGuaranteesExactlyOneCurrentArtifact()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"visual_concurrency_a_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath};";

        var dbOptions = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            using (var setupDb = new ProjectDbContext(dbOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
            }

            var sessionId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            var charId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            var jobB = new ImageGenerationJob(sessionId, turnId, charId, 1, Guid.NewGuid());
            jobB.TryClaim("worker-B", TimeSpan.FromMinutes(2), now);

            var attemptB = new ImageGenerationAttempt(jobB.Id, turnId, 1, 1, 1000L, "{}", "fp-B", GenerationAttemptStatus.Succeeded, claimedBy: "worker-B");

            var jobC = new ImageGenerationJob(sessionId, turnId, charId, 1, Guid.NewGuid());
            jobC.TryClaim("worker-C", TimeSpan.FromMinutes(2), now);

            var attemptC = new ImageGenerationAttempt(jobC.Id, turnId, 1, 1, 2000L, "{}", "fp-C", GenerationAttemptStatus.Succeeded, claimedBy: "worker-C");

            using (var seedDb = new ProjectDbContext(dbOptions))
            {
                await seedDb.ImageGenerationJobs.AddRangeAsync(jobB, jobC);
                await seedDb.ImageGenerationAttempts.AddRangeAsync(attemptB, attemptC);
                await seedDb.SaveChangesAsync();
            }

            var snapshot = CreateTestSnapshot(sessionId, turnId, charId);

            // 2. Run concurrent acceptance in parallel with separate DbContexts on separate connections
            var taskB = Task.Run(async () =>
            {
                await using var dbB = new ProjectDbContext(dbOptions);
                var serviceB = new ArtifactAcceptanceService(dbB, new SystemDateTimeProvider(), NullLogger<ArtifactAcceptanceService>.Instance);
                var reqB = new ArtifactAcceptanceRequest(jobB.Id, attemptB.Id, snapshot, "https://cdn.project00.ai/B.png", "prompt B", null, "fp-B", "{}", true, "worker-B", Guid.NewGuid(), null);
                return await serviceB.AcceptAttemptAtomicallyAsync(reqB);
            });

            var taskC = Task.Run(async () =>
            {
                await using var dbC = new ProjectDbContext(dbOptions);
                var serviceC = new ArtifactAcceptanceService(dbC, new SystemDateTimeProvider(), NullLogger<ArtifactAcceptanceService>.Instance);
                var reqC = new ArtifactAcceptanceRequest(jobC.Id, attemptC.Id, snapshot, "https://cdn.project00.ai/C.png", "prompt C", null, "fp-C", "{}", true, "worker-C", Guid.NewGuid(), null);
                return await serviceC.AcceptAttemptAtomicallyAsync(reqC);
            });

            var results = await Task.WhenAll(taskB, taskC);

            // 3. Invariant Verification: Exactly ONE Current artifact exists in the session in DB
            using (var verifyDb = new ProjectDbContext(dbOptions))
            {
                var currentArtifacts = await verifyDb.SceneImages
                    .Where(img => img.SessionId == sessionId && img.IsCurrent)
                    .ToListAsync();

                Assert.Single(currentArtifacts);

                var sessionState = await verifyDb.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
                Assert.Equal(currentArtifacts[0].Id, sessionState.CurrentImageId);
                Assert.True(sessionState.VisualRevision >= 1);
            }
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task ScenarioB_RegenerationVsCancel_CancelledJobCannotPromoteArtifact()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"visual_concurrency_b_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath};";

        var dbOptions = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            using (var setupDb = new ProjectDbContext(dbOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
            }

            var sessionId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            var charId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
            job.TryClaim("worker-1", TimeSpan.FromMinutes(2), now);
            job.RequestCancellation(now);

            var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-cancel", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");

            using (var seedDb = new ProjectDbContext(dbOptions))
            {
                await seedDb.ImageGenerationJobs.AddAsync(job);
                await seedDb.ImageGenerationAttempts.AddAsync(attempt);
                await seedDb.SaveChangesAsync();
            }

            var snapshot = CreateTestSnapshot(sessionId, turnId, charId);

            using (var db = new ProjectDbContext(dbOptions))
            {
                var service = new ArtifactAcceptanceService(db, new SystemDateTimeProvider(), NullLogger<ArtifactAcceptanceService>.Instance);
                var req = new ArtifactAcceptanceRequest(job.Id, attempt.Id, snapshot, "https://cdn.project00.ai/cancelled.png", "prompt", null, "fp-cancel", "{}", true, "worker-1", Guid.NewGuid(), null);

                var res = await service.AcceptAttemptAtomicallyAsync(req);
                Assert.Equal(JobExecutionStatus.Deferred, res.Status);
            }

            using (var verifyDb = new ProjectDbContext(dbOptions))
            {
                var currentCount = await verifyDb.SceneImages.CountAsync(img => img.SessionId == sessionId && img.IsCurrent);
                Assert.Equal(0, currentCount);
            }
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task ScenarioC_OldGenerationCompletesLate_DoesNotResurrectAsCurrent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"visual_concurrency_c_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath};";

        var dbOptions = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            using (var setupDb = new ProjectDbContext(dbOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
            }

            var sessionId = Guid.NewGuid();
            var turnId1 = Guid.NewGuid();
            var turnId2 = Guid.NewGuid();
            var charId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            // Job A for Turn 1 started earlier with expired lease
            var jobA = new ImageGenerationJob(sessionId, turnId1, charId, 1);
            jobA.TryClaim("worker-A", TimeSpan.FromMinutes(1), now.AddMinutes(-5));

            var attemptA = new ImageGenerationAttempt(jobA.Id, turnId1, 1, 1, 1000L, "{}", "fp-A", GenerationAttemptStatus.Succeeded, claimedBy: "worker-A");

            // Job B for Turn 2 started later and is ALREADY ACCEPTED (VisualRevision = 2)
            var jobB = new ImageGenerationJob(sessionId, turnId2, charId, 2);
            jobB.TryClaim("worker-B", TimeSpan.FromMinutes(2), now);

            var attemptB = new ImageGenerationAttempt(jobB.Id, turnId2, 2, 1, 2000L, "{}", "fp-B", GenerationAttemptStatus.Succeeded, claimedBy: "worker-B");

            var artifactB = new SceneImage(sessionId, charId, turnId2, 2, "https://cdn.project00.ai/B.png", "prompt B", generationJobId: jobB.Id, visualRevision: 2, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
            var state = new VisualSessionState(sessionId, artifactB.Id, jobB.Id, visualRevision: 2);

            using (var seedDb = new ProjectDbContext(dbOptions))
            {
                await seedDb.ImageGenerationJobs.AddRangeAsync(jobA, jobB);
                await seedDb.ImageGenerationAttempts.AddRangeAsync(attemptA, attemptB);
                await seedDb.SaveChangesAsync();

                jobB.AcceptAttempt(attemptB.Id, now, "worker-B", "{}");
                await seedDb.SceneImages.AddAsync(artifactB);
                await seedDb.VisualSessionStates.AddAsync(state);
                await seedDb.SaveChangesAsync();
            }

            // Worker A finishes late
            var snapshotA = CreateTestSnapshot(sessionId, turnId1, charId, 1);
            using (var dbA = new ProjectDbContext(dbOptions))
            {
                var serviceA = new ArtifactAcceptanceService(dbA, new SystemDateTimeProvider(), NullLogger<ArtifactAcceptanceService>.Instance);
                var reqA = new ArtifactAcceptanceRequest(jobA.Id, attemptA.Id, snapshotA, "https://cdn.project00.ai/A.png", "prompt A", null, "fp-A", "{}", true, "worker-A", Guid.NewGuid(), null);
                var resA = await serviceA.AcceptAttemptAtomicallyAsync(reqA);
                // Expired lease -> deferred
                Assert.Equal(JobExecutionStatus.Deferred, resA.Status);
            }

            // Invariant: Job B remains the authoritative Current image in DB
            using (var verifyDb = new ProjectDbContext(dbOptions))
            {
                var reloadedArtifactB = await verifyDb.SceneImages.FirstAsync(img => img.Id == artifactB.Id);
                Assert.True(reloadedArtifactB.IsCurrent);

                var reloadedState = await verifyDb.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
                Assert.Equal(artifactB.Id, reloadedState.CurrentImageId);
                Assert.Equal(2, reloadedState.VisualRevision);
            }
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }
}
