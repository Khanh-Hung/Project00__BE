using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class VisualPredecessorResolverTests
{
    private static (ProjectDbContext Db, VisualPredecessorResolver Resolver) CreateContext()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var resolver = new VisualPredecessorResolver(db, NullLogger<VisualPredecessorResolver>.Instance);
        return (db, resolver);
    }

    private static VisualSnapshot CreateTestSnapshot(
        Guid sessionId,
        Guid turnId,
        Guid characterId,
        string? previousUrl = null,
        string? identityRef = null)
    {
        return new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: 1,
            VisualIdentity: new CharacterVisualIdentity(CanonicalReferenceUrl: identityRef),
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 1000L),
            IdentityReferenceUrl: identityRef,
            PreviousSceneImageUrl: previousUrl
        );
    }

    [Fact]
    public async Task Tier1_ResolvesExplicitPredecessor_WhenValidInSameSession()
    {
        var (db, resolver) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var artifact = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/explicit_img.png",
            prompt: "1girl knight",
            visualRevision: 2,
            lifecycleStatus: ArtifactLifecycleStatus.Historical
        );
        await db.SceneImages.AddAsync(artifact);
        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId, previousUrl: artifact.ImageUrl);
        var resolved = await resolver.ResolveAsync(sessionId, turnId, snapshot);

        Assert.NotNull(resolved);
        Assert.Equal(artifact.Id, resolved.ArtifactId);
        Assert.Equal("https://cdn.project00.ai/explicit_img.png", resolved.ImageUrl);
        Assert.Equal("SnapshotExplicit", resolved.Source);
        Assert.Equal(2, resolved.VisualRevision);
    }

    [Fact]
    public async Task Tier1_RejectsCrossSessionExplicitPredecessor_AndFallsBack()
    {
        var (db, resolver) = CreateContext();
        var sessionIdA = Guid.NewGuid();
        var sessionIdB = Guid.NewGuid(); // Foreign session
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var foreignArtifact = new SceneImage(
            sessionId: sessionIdB,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/foreign_img.png",
            prompt: "1girl knight"
        );
        await db.SceneImages.AddAsync(foreignArtifact);
        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionIdA, turnId, charId, previousUrl: foreignArtifact.ImageUrl, identityRef: "https://cdn.project00.ai/canonical.png");
        var resolved = await resolver.ResolveAsync(sessionIdA, turnId, snapshot);

        Assert.NotNull(resolved);
        Assert.Equal("CharacterCanonicalReference", resolved.Source);
        Assert.Equal("https://cdn.project00.ai/canonical.png", resolved.ImageUrl);
    }

    [Fact]
    public async Task Tier1_RejectsQuarantinedOrDeletedExplicitPredecessor()
    {
        var (db, resolver) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var quarantinedArtifact = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/quarantined.png",
            prompt: "1girl knight",
            lifecycleStatus: ArtifactLifecycleStatus.Quarantined,
            isCurrent: false
        );
        await db.SceneImages.AddAsync(quarantinedArtifact);
        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId, previousUrl: quarantinedArtifact.ImageUrl, identityRef: "https://cdn.project00.ai/canonical.png");
        var resolved = await resolver.ResolveAsync(sessionId, turnId, snapshot);

        Assert.NotNull(resolved);
        Assert.Equal("CharacterCanonicalReference", resolved.Source);
    }

    [Fact]
    public async Task Tier2_ResolvesCurrentSessionArtifact_ViaVisualSessionState()
    {
        var (db, resolver) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var currentArtifact = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 2,
            imageUrl: "https://cdn.project00.ai/current_scene.png",
            prompt: "1girl knight",
            visualRevision: 5,
            lifecycleStatus: ArtifactLifecycleStatus.Current,
            isCurrent: true
        );
        await db.SceneImages.AddAsync(currentArtifact);

        var state = new VisualSessionState(sessionId, currentImageId: currentArtifact.Id, visualRevision: 5);
        await db.VisualSessionStates.AddAsync(state);
        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId);
        var resolved = await resolver.ResolveAsync(sessionId, turnId, snapshot);

        Assert.NotNull(resolved);
        Assert.Equal(currentArtifact.Id, resolved.ArtifactId);
        Assert.Equal("https://cdn.project00.ai/current_scene.png", resolved.ImageUrl);
        Assert.Equal("CurrentSessionArtifact", resolved.Source);
        Assert.Equal(5, resolved.VisualRevision);
    }

    [Fact]
    public async Task Tier3_ResolvesCharacterCanonicalReference_WhenNoSessionArtifactExists()
    {
        var (db, resolver) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId, identityRef: "https://cdn.project00.ai/character_face_crop.png");
        var resolved = await resolver.ResolveAsync(sessionId, turnId, snapshot);

        Assert.NotNull(resolved);
        Assert.Null(resolved.ArtifactId);
        Assert.Equal("https://cdn.project00.ai/character_face_crop.png", resolved.ImageUrl);
        Assert.Equal("CharacterCanonicalReference", resolved.Source);
    }

    [Fact]
    public async Task Tier4_ReturnsNull_WhenNoPredecessorOrCanonicalReferenceExists()
    {
        var (db, resolver) = CreateContext();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId, previousUrl: null, identityRef: null);
        var resolved = await resolver.ResolveAsync(sessionId, turnId, snapshot);

        Assert.Null(resolved);
    }
}
