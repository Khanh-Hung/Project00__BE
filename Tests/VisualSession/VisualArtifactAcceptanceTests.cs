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

public sealed class VisualArtifactAcceptanceTests
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
            GenerationProfile: GenerationProfile.CreateDefault(seed: 5000L)
        );
    }

    [Fact]
    public async Task AcceptAttempt_PromotesArtifactToCurrent_AndInitializesVisualSessionStateAtRevision1()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var genRequestId = Guid.NewGuid();
        var workerId = "worker-acceptance-1";

        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, genRequestId);
        job.TryClaim(workerId, TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(job);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 5000L, "{}", "fp-1", GenerationAttemptStatus.Succeeded, claimedBy: workerId);
        await db.ImageGenerationAttempts.AddAsync(attempt);
        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionId, turnId, characterId, 1);
        var request = new ArtifactAcceptanceRequest(
            JobId: job.Id,
            WinningAttemptId: attempt.Id,
            Snapshot: snapshot,
            ImageUrl: "https://cdn.project00.ai/scene_1.png",
            CompiledPrompt: "1girl knight",
            ResolvedPreviousSceneImageUrl: null,
            GenerationFingerprint: "fp-1",
            MetadataJson: "{}",
            IsIdentityPassed: true,
            WorkerId: workerId,
            OutboxId: Guid.NewGuid(),
            Provenance: null
        );

        var result = await service.AcceptAttemptAtomicallyAsync(request);

        Assert.Equal(JobExecutionStatus.Completed, result.Status);

        // Verify SceneImage state
        var artifact = await db.SceneImages.FirstAsync(img => img.GenerationJobId == job.Id);
        Assert.True(artifact.IsCurrent);
        Assert.Equal(ArtifactLifecycleStatus.Current, artifact.LifecycleStatus);
        Assert.Equal(1, artifact.VisualRevision);

        // Verify VisualSessionState: must be 1, NOT 2 (No double increment!)
        var sessionState = await db.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
        Assert.Equal(artifact.Id, sessionState.CurrentImageId);
        Assert.Equal(job.Id, sessionState.CurrentGenerationJobId);
        Assert.Equal(1, sessionState.VisualRevision);
    }

    [Fact]
    public async Task AcceptAttempt_DemotesPreviousCurrentArtifact_AndAdvancesVisualRevisionExactlyOnce()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId1 = Guid.NewGuid();
        var turnId2 = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var workerId = "worker-acceptance-2";

        // Previous current artifact at VisualRevision 1
        var previousArtifact = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: turnId1,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/prev_scene.png",
            prompt: "1girl standing",
            visualRevision: 1,
            isCurrent: true,
            lifecycleStatus: ArtifactLifecycleStatus.Current
        );
        await db.SceneImages.AddAsync(previousArtifact);

        var existingState = new VisualSessionState(sessionId, previousArtifact.Id, Guid.NewGuid(), visualRevision: 1);
        await db.VisualSessionStates.AddAsync(existingState);

        // New job for Turn 2
        var job2 = new ImageGenerationJob(sessionId, turnId2, characterId, 2, Guid.NewGuid());
        job2.TryClaim(workerId, TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(job2);

        var attempt2 = new ImageGenerationAttempt(job2.Id, turnId2, 2, 1, 6000L, "{}", "fp-2", GenerationAttemptStatus.Succeeded, claimedBy: workerId);
        await db.ImageGenerationAttempts.AddAsync(attempt2);
        await db.SaveChangesAsync();

        var snapshot2 = CreateTestSnapshot(sessionId, turnId2, characterId, 2);
        var request = new ArtifactAcceptanceRequest(
            JobId: job2.Id,
            WinningAttemptId: attempt2.Id,
            Snapshot: snapshot2,
            ImageUrl: "https://cdn.project00.ai/new_scene.png",
            CompiledPrompt: "1girl running",
            ResolvedPreviousSceneImageUrl: previousArtifact.ImageUrl,
            GenerationFingerprint: "fp-2",
            MetadataJson: "{}",
            IsIdentityPassed: true,
            WorkerId: workerId,
            OutboxId: Guid.NewGuid(),
            Provenance: null
        );

        var result = await service.AcceptAttemptAtomicallyAsync(request);

        Assert.Equal(JobExecutionStatus.Completed, result.Status);

        // Previous artifact demoted to Historical
        var reloadedPrevious = await db.SceneImages.FirstAsync(img => img.Id == previousArtifact.Id);
        Assert.False(reloadedPrevious.IsCurrent);
        Assert.Equal(ArtifactLifecycleStatus.Historical, reloadedPrevious.LifecycleStatus);

        // New artifact promoted to Current with VisualRevision 2 (exactly 1 increment: 1 -> 2)
        var newArtifact = await db.SceneImages.FirstAsync(img => img.GenerationJobId == job2.Id);
        Assert.True(newArtifact.IsCurrent);
        Assert.Equal(ArtifactLifecycleStatus.Current, newArtifact.LifecycleStatus);
        Assert.Equal(2, newArtifact.VisualRevision);
        Assert.Equal(previousArtifact.Id, newArtifact.PredecessorArtifactId);

        // VisualSessionState updated to revision 2 (matching newArtifact.VisualRevision!)
        var reloadedState = await db.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
        Assert.Equal(newArtifact.Id, reloadedState.CurrentImageId);
        Assert.Equal(2, reloadedState.VisualRevision);
    }

    [Fact]
    public async Task QuarantinedAttempt_DoesNotPromote_SetsQuarantinedAt_AndPreservesExistingSessionState()
    {
        var (db, service) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var workerId = "worker-quarantine";

        // Initial active session state
        var activeArtifact = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: Guid.NewGuid(),
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/active.png",
            prompt: "1girl",
            visualRevision: 1,
            isCurrent: true,
            lifecycleStatus: ArtifactLifecycleStatus.Current
        );
        await db.SceneImages.AddAsync(activeArtifact);

        var state = new VisualSessionState(sessionId, activeArtifact.Id, Guid.NewGuid(), visualRevision: 1);
        await db.VisualSessionStates.AddAsync(state);

        var job = new ImageGenerationJob(sessionId, turnId, characterId, 2, Guid.NewGuid());
        job.TryClaim(workerId, TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(job);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 2, 3, 7000L, "{}", "fp-quarantine", GenerationAttemptStatus.Degraded, claimedBy: workerId);
        await db.ImageGenerationAttempts.AddAsync(attempt);
        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionId, turnId, characterId, 2);
        var request = new ArtifactAcceptanceRequest(
            JobId: job.Id,
            WinningAttemptId: attempt.Id,
            Snapshot: snapshot,
            ImageUrl: "https://cdn.project00.ai/degraded.png",
            CompiledPrompt: "1girl blurry",
            ResolvedPreviousSceneImageUrl: null,
            GenerationFingerprint: "fp-quarantine",
            MetadataJson: "{}",
            IsIdentityPassed: false, // Quality guard failed!
            WorkerId: workerId,
            OutboxId: Guid.NewGuid(),
            Provenance: null
        );

        var result = await service.AcceptAttemptAtomicallyAsync(request);

        Assert.Equal(JobExecutionStatus.Completed, result.Status);

        // Verify new artifact is marked Quarantined with QuarantinedAt timestamp
        var quarantinedArtifact = await db.SceneImages.FirstAsync(img => img.GenerationJobId == job.Id);
        Assert.False(quarantinedArtifact.IsCurrent);
        Assert.Equal(ArtifactLifecycleStatus.Quarantined, quarantinedArtifact.LifecycleStatus);
        Assert.NotNull(quarantinedArtifact.QuarantinedAt);

        // Verify existing session state remains untouched
        var reloadedState = await db.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
        Assert.Equal(activeArtifact.Id, reloadedState.CurrentImageId);
        Assert.Equal(1, reloadedState.VisualRevision);
    }
}
