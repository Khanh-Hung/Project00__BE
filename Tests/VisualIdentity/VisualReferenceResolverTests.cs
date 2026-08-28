using Application.DTOs;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualIdentity;

public sealed class VisualReferenceResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public VisualReferenceResolverTests()
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
    public async Task ResolveAsync_WhenZeroCanonicalExists_ReturnsNullPrimaryIdentityReference()
    {
        await using var db = new CoreDbContext(_options);
        var resolver = new CharacterVisualReferenceResolver(db, NullLogger<CharacterVisualReferenceResolver>.Instance);
        var charId = Guid.NewGuid();

        var secondary = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/secondary.png",
            type: VisualReferenceType.SecondaryCanonical,
            status: VisualReferenceStatus.Active,
            isCanonical: false,
            priority: 10
        );

        var sceneEvidence = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/scene_evidence.png",
            type: VisualReferenceType.GeneratedEvidence,
            status: VisualReferenceStatus.Active,
            isCanonical: false,
            priority: 50 // Even with high priority
        );

        db.CharacterVisualReferences.AddRange(secondary, sceneEvidence);
        await db.SaveChangesAsync();

        var result = await resolver.ResolveAsync(charId, new VisualReferenceContext());

        // Invariant: PrimaryIdentityReference MUST be null if no canonical identity reference exists
        Assert.Null(result.PrimaryIdentityReference);

        // Secondary and scene evidence are categorized into their respective bounded sets
        Assert.Single(result.SecondaryReferences);
        Assert.Equal(secondary.Id, result.SecondaryReferences[0].ReferenceId);

        Assert.Single(result.SceneReferences);
        Assert.Equal(sceneEvidence.Id, result.SceneReferences[0].ReferenceId);
    }

    [Fact]
    public async Task ResolveAsync_WhenOneCanonicalExists_SelectsAsPrimaryWithDominatingScore()
    {
        await using var db = new CoreDbContext(_options);
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
    public async Task ResolveAsync_WhenArchivedCanonicalAndActiveCanonicalExist_SelectsOnlyActiveCanonical()
    {
        await using var db = new CoreDbContext(_options);
        var resolver = new CharacterVisualReferenceResolver(db, NullLogger<CharacterVisualReferenceResolver>.Instance);
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var archivedCanonical = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/archived_canonical.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Archived,
            isCanonical: false,
            priority: 100 // High priority but archived
        );

        var activeCanonical = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/active_canonical.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true,
            priority: 1,
            now: now
        );

        db.CharacterVisualReferences.AddRange(archivedCanonical, activeCanonical);
        await db.SaveChangesAsync();

        var result = await resolver.ResolveAsync(charId, new VisualReferenceContext());

        Assert.NotNull(result.PrimaryIdentityReference);
        Assert.Equal(activeCanonical.Id, result.PrimaryIdentityReference.ReferenceId);
        Assert.Equal(activeCanonical.ReferenceUrl, result.PrimaryIdentityReference.ReferenceUrl);
    }

    [Fact]
    public async Task ResolveAsync_CanonicalReferenceAlwaysBeatsRecentGeneratedEvidence()
    {
        await using var db = new CoreDbContext(_options);
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
        await using var db = new CoreDbContext(_options);
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
