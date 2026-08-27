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

namespace Tests.VisualIdentity;

public sealed class GenerationVisualIdentityIntegrationTests
{
    private static ProjectDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProjectDbContext(options);
    }

    [Fact]
    public async Task GenerationFlow_CreatesVisualEvidence_WithoutAlteringCanonicalIdentity()
    {
        using var db = CreateInMemoryDb();
        var dateTimeProvider = new SystemDateTimeProvider();
        var profileService = new CharacterVisualProfileService(db, NullLogger<CharacterVisualProfileService>.Instance);
        var referenceService = new CharacterVisualReferenceService(db, profileService, NullLogger<CharacterVisualReferenceService>.Instance);
        var resolver = new CharacterVisualReferenceResolver(db, NullLogger<CharacterVisualReferenceResolver>.Instance);
        var acceptanceService = new ArtifactAcceptanceService(db, dateTimeProvider, NullLogger<ArtifactAcceptanceService>.Instance);

        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // 1. Establish Character & Authoritative Visual Profile
        var profile = await profileService.CreateProfileAsync(charId, "Silver hair", "Red eyes", "Pale", "Athletic", "Scar");
        Assert.Equal(1, profile.VisualVersion);

        // 2. Register Canonical Reference
        var canonicalRef = await referenceService.RegisterReferenceAsync(new RegisterVisualReferenceRequest(
            CharacterId: charId,
            ReferenceUrl: "https://cdn.project00.ai/canonical_anchor.png",
            IsCanonical: true,
            Type: VisualReferenceType.Canonical
        ));

        // 3. Resolve Reference for Generation
        var resolvedSet = await resolver.ResolveAsync(charId, new VisualReferenceContext(SceneRevision: 1));
        Assert.NotNull(resolvedSet.PrimaryIdentityReference);
        Assert.Equal(canonicalRef.Id, resolvedSet.PrimaryIdentityReference.ReferenceId);
        Assert.True(resolvedSet.PrimaryIdentityReference.IsCanonical);

        // 4. Run Generation & Acceptance Pipeline
        var job = new ImageGenerationJob(sessionId, turnId, charId, 1, generationRequestId: Guid.NewGuid());
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), now);

        var attempt = new ImageGenerationAttempt(
            job.Id, turnId, 1, 1, 12345L, "{}", "fp-turn1",
            GenerationAttemptStatus.Running, claimedBy: "worker-1", startedAt: now, leaseUntil: now.AddMinutes(2));

        attempt.StartEvaluating("worker-1", now);
        attempt.MarkSucceeded("https://cdn.project00.ai/scene_turn1.png", "pjob-1", 0.94f, 0.91f, now, "worker-1", now);

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);
        await db.SaveChangesAsync();

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: charId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("Throne Room", "Standing", "Royal Robes"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 12345L)
        );

        var request = new ArtifactAcceptanceRequest(
            JobId: job.Id,
            WinningAttemptId: attempt.Id,
            Snapshot: snapshot,
            ImageUrl: "https://cdn.project00.ai/scene_turn1.png",
            CompiledPrompt: "royal throne room, silver hair, red eyes",
            ResolvedPreviousSceneImageUrl: null,
            GenerationFingerprint: attempt.GenerationFingerprint,
            MetadataJson: "{}",
            IsIdentityPassed: true,
            WorkerId: "worker-1",
            OutboxId: Guid.NewGuid(),
            Provenance: null
        );

        var result = await acceptanceService.AcceptAttemptAtomicallyAsync(request, CancellationToken.None);
        Assert.Equal(JobExecutionStatus.Completed, result.Status);

        // 5. Verify Visual Evidence / Memory was Recorded
        var memories = await db.CharacterVisualMemories.Where(m => m.CharacterId == charId).ToListAsync();
        Assert.Single(memories);
        Assert.Equal(2, memories[0].VisualProfileVersion);
        Assert.Equal(1, memories[0].SceneRevision);
        Assert.Equal("Throne Room - Royal Robes", memories[0].Context);

        // 6. Invariant Verification: Visual Profile was NOT modified by generation
        var currentProfile = await profileService.GetCurrentProfileAsync(charId);
        Assert.NotNull(currentProfile);
        Assert.Equal(2, currentProfile.VisualVersion); // 1 (initial) + 1 (canonical reference set) = 2
        Assert.Equal(canonicalRef.Id, currentProfile.PrimaryReferenceId);
    }
}
