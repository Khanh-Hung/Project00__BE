using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualIdentity;

public sealed class VisualReferenceResolverTests
{
    private static ProjectDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProjectDbContext(options);
    }

    [Fact]
    public async Task ResolveAsync_WhenCanonicalReferenceExists_SelectsAsPrimaryWithDominatingScore()
    {
        using var db = CreateInMemoryDb();
        var resolver = new CharacterVisualReferenceResolver(db, NullLogger<CharacterVisualReferenceResolver>.Instance);
        var charId = Guid.NewGuid();

        var canonical = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/canonical.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true,
            priority: 10
        );

        var secondary = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/secondary.png",
            type: VisualReferenceType.SecondaryCanonical,
            status: VisualReferenceStatus.Active,
            isCanonical: false,
            priority: 5
        );

        db.CharacterVisualReferences.AddRange(canonical, secondary);
        await db.SaveChangesAsync();

        var context = new VisualReferenceContext(SceneRevision: 1);
        var result = await resolver.ResolveAsync(charId, context);

        Assert.NotNull(result.PrimaryIdentityReference);
        Assert.Equal(canonical.Id, result.PrimaryIdentityReference.ReferenceId);
        Assert.True(result.PrimaryIdentityReference.IsCanonical);
        Assert.True(result.PrimaryIdentityReference.Score >= 1000f);

        Assert.Single(result.SecondaryReferences);
        Assert.Equal(secondary.Id, result.SecondaryReferences[0].ReferenceId);
    }

    [Fact]
    public async Task ResolveAsync_CanonicalReferenceAlwaysBeatsRecentGeneratedEvidence()
    {
        using var db = CreateInMemoryDb();
        var resolver = new CharacterVisualReferenceResolver(db, NullLogger<CharacterVisualReferenceResolver>.Instance);
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var canonical = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/canonical.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true,
            priority: 1,
            now: now.AddDays(-10) // 10 days older
        );

        var recentEvidence = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/recent_scene_helmet.png",
            type: VisualReferenceType.GeneratedEvidence,
            status: VisualReferenceStatus.Active,
            isCanonical: false,
            priority: 20, // Higher priority value
            now: now // Created just now
        );

        db.CharacterVisualReferences.AddRange(canonical, recentEvidence);
        await db.SaveChangesAsync();

        var context = new VisualReferenceContext(SceneRevision: 5);
        var result = await resolver.ResolveAsync(charId, context);

        // Invariant: Canonical reference MUST dominate as primary identity reference
        Assert.NotNull(result.PrimaryIdentityReference);
        Assert.Equal(canonical.Id, result.PrimaryIdentityReference.ReferenceId);
        Assert.True(result.PrimaryIdentityReference.IsCanonical);

        // Recent evidence is categorized under SceneReferences, not replacing PrimaryIdentityReference
        Assert.Single(result.SceneReferences);
        Assert.Equal(recentEvidence.Id, result.SceneReferences[0].ReferenceId);
    }

    [Fact]
    public async Task ResolveAsync_RespectsSecondaryAndSceneLimits()
    {
        using var db = CreateInMemoryDb();
        var resolver = new CharacterVisualReferenceResolver(db, NullLogger<CharacterVisualReferenceResolver>.Instance);
        var charId = Guid.NewGuid();

        for (int i = 0; i < 5; i++)
        {
            db.CharacterVisualReferences.Add(new CharacterVisualReference(
                characterId: charId,
                referenceUrl: $"https://cdn.project00.ai/sec_{i}.png",
                type: VisualReferenceType.SecondaryCanonical,
                priority: i
            ));
            db.CharacterVisualReferences.Add(new CharacterVisualReference(
                characterId: charId,
                referenceUrl: $"https://cdn.project00.ai/scene_{i}.png",
                type: VisualReferenceType.SceneReference,
                priority: i
            ));
        }
        await db.SaveChangesAsync();

        var context = new VisualReferenceContext(
            MaxSecondaryReferences: 2,
            MaxSceneReferences: 2
        );

        var result = await resolver.ResolveAsync(charId, context);

        Assert.Equal(2, result.SecondaryReferences.Count);
        Assert.Equal(2, result.SceneReferences.Count);
    }
}
