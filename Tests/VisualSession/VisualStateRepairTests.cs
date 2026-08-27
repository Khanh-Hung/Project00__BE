using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class VisualStateRepairTests
{
    private static ProjectDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProjectDbContext(options);
    }

    [Fact]
    public async Task RepairVisualState_WhenRepairableDueToMissingStateEntity_RestoresStateAndTransitionsToHealthy()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-repair-1", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", generationJobId: job.Id, generationAttemptId: attempt.Id, visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        attempt.AttachAcceptedArtifact(artifact.Id, DateTime.UtcNow);
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        db.SceneImages.Add(artifact);
        // Note: VisualSessionState is intentionally omitted
        await db.SaveChangesAsync();

        // 1. Initial diagnose returns Repairable
        var diagnosis = await service.ValidateConsistencyAsync(sessionId);
        Assert.Equal(VisualStateConsistencyStatus.Repairable, diagnosis.Status);
        Assert.Equal(artifact.Id, diagnosis.ExpectedArtifactId);
        Assert.Equal(job.Id, diagnosis.ExpectedJobId);

        // 2. Act: Execute deterministic repair
        var repairResult = await service.RepairVisualStateAsync(sessionId);
        Assert.Equal(VisualStateConsistencyStatus.Healthy, repairResult.Status);
        Assert.Equal(artifact.Id, repairResult.CurrentArtifactId);

        // 3. Post-repair verification: State entity exists in DB and diagnose returns Healthy
        var state = await db.VisualSessionStates.FirstOrDefaultAsync(s => s.SessionId == sessionId);
        Assert.NotNull(state);
        Assert.Equal(artifact.Id, state.CurrentImageId);
        Assert.Equal(1, state.VisualRevision);

        var postDiagnosis = await service.ValidateConsistencyAsync(sessionId);
        Assert.Equal(VisualStateConsistencyStatus.Healthy, postDiagnosis.Status);
    }

    [Fact]
    public async Task RepairVisualState_WhenStateIsCorrupted_ThrowsInvalidOperationException()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        // Foreign session artifact
        var foreignArtifact = new SceneImage(foreignSession, charId, turnId, 1, "https://cdn.project00.ai/foreign.png", "prompt", visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        var sessionState = new VisualSessionState(sessionId, foreignArtifact.Id, Guid.NewGuid(), visualRevision: 1);

        db.SceneImages.Add(foreignArtifact);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        // Act & Assert: Service refuses to guess/repair corrupted lineage
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairVisualStateAsync(sessionId));
        Assert.Contains("Corrupted", ex.Message);
    }

    [Fact]
    public async Task RepairVisualState_WhenSessionStateAdvancedToNewerRevision_AbortsRepairToPreventStateRollback()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        // Job 1 / Revision 1
        var job1 = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job1.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        var attempt1 = new ImageGenerationAttempt(job1.Id, turnId, 1, 1, 1000L, "{}", "fp-rev1", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifact1 = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art1.png", "prompt", generationJobId: job1.Id, generationAttemptId: attempt1.Id, visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Historical);
        attempt1.AttachAcceptedArtifact(artifact1.Id, DateTime.UtcNow);
        job1.AcceptAttempt(attempt1.Id, DateTime.UtcNow, "worker-1", "{}");

        // Seed DB without VisualSessionState initially
        db.ImageGenerationJobs.Add(job1);
        db.ImageGenerationAttempts.Add(attempt1);
        db.SceneImages.Add(artifact1);
        await db.SaveChangesAsync();

        // 1. Initial diagnosis identifies Revision 1 as repairable
        var diagnosis = await service.ValidateConsistencyAsync(sessionId);
        Assert.Equal(VisualStateConsistencyStatus.Repairable, diagnosis.Status);
        Assert.Equal(1, diagnosis.ExpectedVisualRevision);

        // 2. Concurrently, a generation worker completes Revision 2 and advances VisualSessionState
        var newerArtifactId = Guid.NewGuid();
        var newerJobId = Guid.NewGuid();
        var advancedState = new VisualSessionState(sessionId, newerArtifactId, newerJobId, visualRevision: 2);
        db.VisualSessionStates.Add(advancedState);
        await db.SaveChangesAsync();

        // 3. Act & Assert: Service refuses to repair / overwrite the newer revision
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairVisualStateAsync(sessionId));
        Assert.NotNull(ex);

        // 4. Verify session state was not rolled back to Revision 1
        var finalState = await db.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
        Assert.Equal(2, finalState.VisualRevision);
        Assert.Equal(newerArtifactId, finalState.CurrentImageId);
    }

    [Fact]
    public async Task RepairVisualState_WhenSessionStatePointsToDifferentJobAtSameRevision_AbortsRepair()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        // Job A / Revision 1
        var jobA = new ImageGenerationJob(sessionId, turnId, charId, 1);
        jobA.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        var attemptA = new ImageGenerationAttempt(jobA.Id, turnId, 1, 1, 1000L, "{}", "fp-a", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifactA = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art_a.png", "prompt", generationJobId: jobA.Id, generationAttemptId: attemptA.Id, visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Historical);
        attemptA.AttachAcceptedArtifact(artifactA.Id, DateTime.UtcNow);
        jobA.AcceptAttempt(attemptA.Id, DateTime.UtcNow, "worker-1", "{}");

        // Seed Job A and Attempt A
        db.ImageGenerationJobs.Add(jobA);
        db.ImageGenerationAttempts.Add(attemptA);
        db.SceneImages.Add(artifactA);
        await db.SaveChangesAsync();

        // Initial diagnosis identifies Job A / Revision 1 as repairable
        var diagnosis = await service.ValidateConsistencyAsync(sessionId);
        Assert.Equal(VisualStateConsistencyStatus.Repairable, diagnosis.Status);
        Assert.Equal(jobA.Id, diagnosis.ExpectedJobId);

        // Concurrently, Job B is set on VisualSessionState at the same Revision 1
        var jobB = new ImageGenerationJob(sessionId, turnId, charId, 1);
        jobB.TryClaim("worker-2", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        var attemptB = new ImageGenerationAttempt(jobB.Id, turnId, 1, 1, 2000L, "{}", "fp-b", GenerationAttemptStatus.Succeeded, claimedBy: "worker-2");
        var artifactB = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art_b.png", "prompt", generationJobId: jobB.Id, generationAttemptId: attemptB.Id, visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        attemptB.AttachAcceptedArtifact(artifactB.Id, DateTime.UtcNow);
        jobB.AcceptAttempt(attemptB.Id, DateTime.UtcNow, "worker-2", "{}");

        var sessionState = new VisualSessionState(sessionId, artifactB.Id, jobB.Id, visualRevision: 1);
        db.ImageGenerationJobs.Add(jobB);
        db.ImageGenerationAttempts.Add(attemptB);
        db.SceneImages.Add(artifactB);
        db.VisualSessionStates.Add(sessionState);
        await db.SaveChangesAsync();

        // Act & Assert: Service detects state is already healthy pointing to Job B != Job A and does not overwrite with Job A
        var repairResult = await service.RepairVisualStateAsync(sessionId);
        Assert.Equal(VisualStateConsistencyStatus.Healthy, repairResult.Status);
        Assert.Equal(artifactB.Id, repairResult.CurrentArtifactId);

        // Verify session state still points to Job B
        var finalState = await db.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
        Assert.Equal(jobB.Id, finalState.CurrentGenerationJobId);
        Assert.Equal(artifactB.Id, finalState.CurrentImageId);
    }

    [Fact]
    public async Task RepairVisualState_WhenNewGenerationAcceptedAfterDiagnosis_PreservesNewGenerationAndNeverRollsBack()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId1 = Guid.NewGuid();
        var turnId2 = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // 1. Generation A (Revision 1)
        var jobA = new ImageGenerationJob(sessionId, turnId1, charId, 1);
        jobA.TryClaim("worker-1", TimeSpan.FromMinutes(2), now);
        var attemptA = new ImageGenerationAttempt(jobA.Id, turnId1, 1, 1, 1000L, "{}", "fp-a", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifactA = new SceneImage(sessionId, charId, turnId1, 1, "https://cdn.project00.ai/art_a.png", "prompt", generationJobId: jobA.Id, generationAttemptId: attemptA.Id, visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Historical);
        attemptA.AttachAcceptedArtifact(artifactA.Id, now);
        jobA.AcceptAttempt(attemptA.Id, now, "worker-1", "{}");

        db.ImageGenerationJobs.Add(jobA);
        db.ImageGenerationAttempts.Add(attemptA);
        db.SceneImages.Add(artifactA);
        await db.SaveChangesAsync();

        // 2. Diagnosis A identifies Generation A as repairable target
        var diagnosisA = await service.ValidateConsistencyAsync(sessionId);
        Assert.Equal(VisualStateConsistencyStatus.Repairable, diagnosisA.Status);
        Assert.Equal(artifactA.Id, diagnosisA.ExpectedArtifactId);
        Assert.Equal(jobA.Id, diagnosisA.ExpectedJobId);
        Assert.Equal(1, diagnosisA.ExpectedVisualRevision);

        // 3. Concurrently, Generation B completes Turn 2, advances VisualSessionState to Revision 2
        var jobB = new ImageGenerationJob(sessionId, turnId2, charId, 2);
        jobB.TryClaim("worker-2", TimeSpan.FromMinutes(2), now);
        var attemptB = new ImageGenerationAttempt(jobB.Id, turnId2, 2, 1, 2000L, "{}", "fp-b", GenerationAttemptStatus.Succeeded, claimedBy: "worker-2");
        var artifactB = new SceneImage(sessionId, charId, turnId2, 2, "https://cdn.project00.ai/art_b.png", "prompt", generationJobId: jobB.Id, generationAttemptId: attemptB.Id, visualRevision: 2, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        attemptB.AttachAcceptedArtifact(artifactB.Id, now);
        jobB.AcceptAttempt(attemptB.Id, now, "worker-2", "{}");

        var sessionStateB = new VisualSessionState(sessionId, artifactB.Id, jobB.Id, visualRevision: 2);

        db.ImageGenerationJobs.Add(jobB);
        db.ImageGenerationAttempts.Add(attemptB);
        db.SceneImages.Add(artifactB);
        db.VisualSessionStates.Add(sessionStateB);
        await db.SaveChangesAsync();

        // 4. Act: Attempt repair using previous diagnosis A -> Service recognizes state is Healthy at Revision 2 and never rolls back to Revision 1
        var repairResult = await service.RepairVisualStateAsync(sessionId);
        Assert.Equal(VisualStateConsistencyStatus.Healthy, repairResult.Status);
        Assert.Equal(artifactB.Id, repairResult.CurrentArtifactId);
        Assert.Equal(2, repairResult.ExpectedVisualRevision);

        // 5. Verify VisualSessionState was preserved at Revision 2 with Artifact B
        var finalState = await db.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
        Assert.Equal(2, finalState.VisualRevision);
        Assert.Equal(artifactB.Id, finalState.CurrentImageId);
        Assert.Equal(jobB.Id, finalState.CurrentGenerationJobId);
    }

    [Fact]
    public async Task RepairVisualState_WhenJobWinnerMismatch_AbortsRepair()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attemptWinner = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-winner", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var attemptLoser = new ImageGenerationAttempt(job.Id, turnId, 1, 2, 2000L, "{}", "fp-loser", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");

        var artifactLoser = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art_loser.png", "prompt", generationJobId: job.Id, generationAttemptId: attemptLoser.Id, visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        attemptLoser.AttachAcceptedArtifact(artifactLoser.Id, DateTime.UtcNow);

        // Job accepted attemptWinner, but attemptLoser attached artifactLoser
        job.AcceptAttempt(attemptWinner.Id, DateTime.UtcNow, "worker-1", "{}");

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.AddRange(attemptWinner, attemptLoser);
        db.SceneImages.Add(artifactLoser);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairVisualStateAsync(sessionId));
        Assert.Contains("Corrupted", ex.Message);
    }

    [Fact]
    public async Task RepairVisualState_WhenArtifactLineageForkDetected_AbortsRepair()
    {
        using var db = CreateInMemoryDb();
        var service = new VisualStateConsistencyService(db, NullLogger<VisualStateConsistencyService>.Instance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attemptA = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-a", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var attemptB = new ImageGenerationAttempt(job.Id, turnId, 1, 2, 2000L, "{}", "fp-b", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");

        // Lineage fork: Artifact says GenerationAttemptId = Attempt B, but Attempt A says AcceptedArtifactId = Artifact
        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art.png", "prompt", generationJobId: job.Id, generationAttemptId: attemptB.Id, visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        attemptA.AttachAcceptedArtifact(artifact.Id, DateTime.UtcNow);
        job.AcceptAttempt(attemptA.Id, DateTime.UtcNow, "worker-1", "{}");

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.AddRange(attemptA, attemptB);
        db.SceneImages.Add(artifact);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairVisualStateAsync(sessionId));
        Assert.Contains("Corrupted", ex.Message);
    }

    [Fact]
    public async Task RepairVisualState_ConcurrentRepairs_PreserveConsistentCurrentArtifact()
    {
        var dbName = Guid.NewGuid().ToString();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-conc-repair", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art_conc.png", "prompt", generationJobId: job.Id, generationAttemptId: attempt.Id, visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Historical);

        attempt.AttachAcceptedArtifact(artifact.Id, DateTime.UtcNow);
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using (var seedDb = new ProjectDbContext(options))
        {
            seedDb.ImageGenerationJobs.Add(job);
            seedDb.ImageGenerationAttempts.Add(attempt);
            seedDb.SceneImages.Add(artifact);
            await seedDb.SaveChangesAsync();
        }

        // 5 concurrent repair workers executing RepairVisualStateAsync
        const int workerCount = 5;
        var tasks = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            using var workerDb = new ProjectDbContext(options);
            var workerService = new VisualStateConsistencyService(workerDb, NullLogger<VisualStateConsistencyService>.Instance);
            return await workerService.RepairVisualStateAsync(sessionId);
        });

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r =>
        {
            Assert.Equal(VisualStateConsistencyStatus.Healthy, r.Status);
            Assert.Equal(artifact.Id, r.CurrentArtifactId);
        });

        // Verification on DB state
        using (var verifyDb = new ProjectDbContext(options))
        {
            var currentArtifacts = await verifyDb.SceneImages.Where(img => img.SessionId == sessionId && img.IsCurrent).ToListAsync();
            Assert.Single(currentArtifacts);
            Assert.Equal(artifact.Id, currentArtifacts[0].Id);

            var sessionState = await verifyDb.VisualSessionStates.FirstAsync(s => s.SessionId == sessionId);
            Assert.Equal(artifact.Id, sessionState.CurrentImageId);
            Assert.Equal(1, sessionState.VisualRevision);
        }
    }
}
