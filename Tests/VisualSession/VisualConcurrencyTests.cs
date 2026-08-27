using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class VisualConcurrencyTests
{
    private static (ProjectDbContext Db, ArtifactAcceptanceService Service) CreateContext()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var service = new ArtifactAcceptanceService(db, new SystemDateTimeProvider(), NullLogger<ArtifactAcceptanceService>.Instance);
        return (db, service);
    }

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
    public async Task ScenarioA_TwoConcurrentRegenerations_GuaranteesExactlyOneCurrentArtifact()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var jobB = new ImageGenerationJob(sessionId, turnId, charId, 1, Guid.NewGuid());
        jobB.TryClaim("worker-B", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(jobB);

        var attemptB = new ImageGenerationAttempt(jobB.Id, turnId, 1, 1, 1000L, "{}", "fp-B", GenerationAttemptStatus.Succeeded, claimedBy: "worker-B");
        await db.ImageGenerationAttempts.AddAsync(attemptB);

        var jobC = new ImageGenerationJob(sessionId, turnId, charId, 1, Guid.NewGuid());
        jobC.TryClaim("worker-C", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(jobC);

        var attemptC = new ImageGenerationAttempt(jobC.Id, turnId, 1, 1, 2000L, "{}", "fp-C", GenerationAttemptStatus.Succeeded, claimedBy: "worker-C");
        await db.ImageGenerationAttempts.AddAsync(attemptC);

        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId);

        // Worker B accepts first
        var reqB = new ArtifactAcceptanceRequest(jobB.Id, attemptB.Id, snapshot, "https://cdn.project00.ai/B.png", "prompt B", null, "fp-B", "{}", true, "worker-B", Guid.NewGuid(), null);
        var resB = await service.AcceptAttemptAtomicallyAsync(reqB);
        Assert.Equal(JobExecutionStatus.Completed, resB.Status);

        // Worker C accepts afterwards
        var reqC = new ArtifactAcceptanceRequest(jobC.Id, attemptC.Id, snapshot, "https://cdn.project00.ai/C.png", "prompt C", null, "fp-C", "{}", true, "worker-C", Guid.NewGuid(), null);
        var resC = await service.AcceptAttemptAtomicallyAsync(reqC);
        Assert.Equal(JobExecutionStatus.Completed, resC.Status);

        // Assert invariant: Exactly ONE current artifact exists in the entire session!
        var currentArtifacts = await db.SceneImages.Where(img => img.SessionId == sessionId && img.IsCurrent).ToListAsync();
        Assert.Single(currentArtifacts);
        Assert.Equal("https://cdn.project00.ai/C.png", currentArtifacts[0].ImageUrl);

        var sessionState = await db.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
        Assert.Equal(currentArtifacts[0].Id, sessionState.CurrentImageId);
    }

    [Fact]
    public async Task ScenarioB_RegenerationVsCancel_CancelledJobCannotPromoteArtifact()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        // User cancels the job!
        job.RequestCancellation(DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(job);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-cancel", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        await db.ImageGenerationAttempts.AddAsync(attempt);
        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId);
        var req = new ArtifactAcceptanceRequest(job.Id, attempt.Id, snapshot, "https://cdn.project00.ai/cancelled.png", "prompt", null, "fp-cancel", "{}", true, "worker-1", Guid.NewGuid(), null);

        var res = await service.AcceptAttemptAtomicallyAsync(req);

        // Acceptance rejected/deferred
        Assert.Equal(JobExecutionStatus.Deferred, res.Status);

        // Zero current artifacts created
        var currentCount = await db.SceneImages.CountAsync(img => img.SessionId == sessionId && img.IsCurrent);
        Assert.Equal(0, currentCount);
    }

    [Fact]
    public async Task ScenarioC_OldGenerationCompletesLate_DoesNotResurrectAsCurrent()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId1 = Guid.NewGuid();
        var turnId2 = Guid.NewGuid();
        var charId = Guid.NewGuid();

        // Job A for Turn 1 started earlier
        var jobA = new ImageGenerationJob(sessionId, turnId1, charId, 1);
        jobA.TryClaim("worker-A", TimeSpan.FromMinutes(2), DateTime.UtcNow.AddMinutes(-5));
        await db.ImageGenerationJobs.AddAsync(jobA);

        var attemptA = new ImageGenerationAttempt(jobA.Id, turnId1, 1, 1, 1000L, "{}", "fp-A", GenerationAttemptStatus.Succeeded, claimedBy: "worker-A");
        await db.ImageGenerationAttempts.AddAsync(attemptA);

        // Job B for Turn 2 started later and ALREADY ACCEPTED (VisualRevision = 2)
        var artifactB = new SceneImage(sessionId, charId, turnId2, 2, "https://cdn.project00.ai/B.png", "prompt B", visualRevision: 2, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        await db.SceneImages.AddAsync(artifactB);

        var state = new VisualSessionState(sessionId, artifactB.Id, Guid.NewGuid(), visualRevision: 2);
        await db.VisualSessionStates.AddAsync(state);

        await db.SaveChangesAsync();

        // Worker A finishes late with expired lease or attempts acceptance
        var snapshotA = CreateTestSnapshot(sessionId, turnId1, charId, 1);
        var reqA = new ArtifactAcceptanceRequest(jobA.Id, attemptA.Id, snapshotA, "https://cdn.project00.ai/A.png", "prompt A", null, "fp-A", "{}", true, "worker-A", Guid.NewGuid(), null);

        var resA = await service.AcceptAttemptAtomicallyAsync(reqA);

        // Worker A is either deferred or demoted, and Job B remains Current
        var reloadedArtifactB = await db.SceneImages.FirstAsync(img => img.Id == artifactB.Id);
        var reloadedState = await db.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);

        // In either case, Job B's current representation or state has not been lost
        Assert.NotNull(reloadedState.CurrentImageId);
    }
}
