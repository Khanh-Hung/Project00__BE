using Application.DTOs;
using Application.Interfaces;
using Application.Services;
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

public sealed class ArtifactLifecycleConsistencyIntegrationTests
{
    private static ProjectDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProjectDbContext(options);
    }

    [Fact]
    public async Task FullLifecycle_EndToEnd_MaintainsAuthoritativeLineageAndConsistency()
    {
        using var db = CreateInMemoryDb();
        var dateTimeProvider = new SystemDateTimeProvider();
        var acceptanceService = new ArtifactAcceptanceService(db, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);
        var consistencyService = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: charId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("garden", "walking"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 777L)
        );

        // 1. Create and claim ImageGenerationJob
        var job = new ImageGenerationJob(
            sessionId: sessionId,
            turnId: turnId,
            characterId: charId,
            sceneRevision: 1,
            generationRequestId: Guid.NewGuid()
        );
        job.TryClaim("worker-1", TimeSpan.FromMinutes(5), now);

        // 2. Create and complete winning attempt
        var attempt = new ImageGenerationAttempt(
            generationJobId: job.Id,
            turnId: turnId,
            sceneRevision: 1,
            attemptNumber: 1,
            derivedSeed: 777L,
            parametersJson: "{}",
            generationFingerprint: "fp-e2e-1",
            status: GenerationAttemptStatus.Running,
            claimedBy: "worker-1",
            startedAt: now,
            leaseUntil: now.AddMinutes(5)
        );
        attempt.StartEvaluating("worker-1", now);
        attempt.MarkSucceeded("https://cdn.project00.ai/e2e.png", "pjob-e2e", 0.96f, 0.92f, now, "worker-1", now);

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        await db.SaveChangesAsync();

        // 3. Execute atomic artifact acceptance
        var acceptRequest = new ArtifactAcceptanceRequest(
            JobId: job.Id,
            WinningAttemptId: attempt.Id,
            Snapshot: snapshot,
            ImageUrl: attempt.ImageUrl!,
            CompiledPrompt: "1girl walking in garden",
            ResolvedPreviousSceneImageUrl: null,
            GenerationFingerprint: attempt.GenerationFingerprint,
            MetadataJson: "{}",
            IsIdentityPassed: true,
            WorkerId: "worker-1",
            OutboxId: Guid.NewGuid(),
            Provenance: null
        );

        var acceptResult = await acceptanceService.AcceptAttemptAtomicallyAsync(acceptRequest, CancellationToken.None);
        Assert.Equal(JobExecutionStatus.Completed, acceptResult.Status);

        // 4. Authoritative Foreign Key chain verification
        var updatedJob = await db.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
        Assert.Equal(attempt.Id, updatedJob.AcceptedAttemptId);

        var updatedAttempt = await db.ImageGenerationAttempts.FirstAsync(a => a.Id == attempt.Id);
        Assert.NotNull(updatedAttempt.AcceptedArtifactId);

        var artifact = await db.SceneImages.FirstAsync(img => img.Id == updatedAttempt.AcceptedArtifactId.Value);
        Assert.Equal(attempt.Id, artifact.GenerationAttemptId);
        Assert.Equal(job.Id, artifact.GenerationJobId);
        Assert.True(artifact.IsCurrent);
        Assert.Equal(ArtifactLifecycleStatus.Current, artifact.LifecycleStatus);

        var sessionState = await db.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
        Assert.Equal(artifact.Id, sessionState.CurrentImageId);
        Assert.Equal(1, sessionState.VisualRevision);

        // 5. Visual state diagnosis confirms Healthy
        var diagnosis = await consistencyService.ValidateConsistencyAsync(sessionId);
        Assert.Equal(VisualStateConsistencyStatus.Healthy, diagnosis.Status);
        Assert.Equal(artifact.Id, diagnosis.CurrentArtifactId);

        // 6. Simulate artifact soft-delete -> Verify diagnosis detects Inconsistent without silent guessing
        artifact.MarkDeleted();
        await db.SaveChangesAsync();

        var postDeleteDiagnosis = await consistencyService.ValidateConsistencyAsync(sessionId);
        Assert.Equal(VisualStateConsistencyStatus.Inconsistent, postDeleteDiagnosis.Status);
    }
}
