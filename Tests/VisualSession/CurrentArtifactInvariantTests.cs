using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tests.VisualSession;

public sealed class CurrentArtifactInvariantTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CurrentArtifactInvariantTests()
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
    public async Task SingleCurrentArtifact_ForSessionAndRevision_IsAllowed()
    {
        await using var db = new CoreDbContext(_options);
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var artifact = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/art1.png", "prompt", visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        db.SceneImages.Add(artifact);
        await db.SaveChangesAsync();

        var saved = await db.SceneImages.FirstOrDefaultAsync(img => img.Id == artifact.Id);
        Assert.NotNull(saved);
        Assert.True(saved.IsCurrent);
    }

    [Fact]
    public async Task MultipleHistoricalArtifacts_ForSameSessionAndRevision_IsAllowed()
    {
        await using var db = new CoreDbContext(_options);
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var historical1 = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/hist1.png", "prompt", generationRequestId: Guid.NewGuid(), visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Historical);
        var historical2 = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/hist2.png", "prompt", generationRequestId: Guid.NewGuid(), visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Historical);
        var current = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/current.png", "prompt", generationRequestId: Guid.NewGuid(), visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        db.SceneImages.AddRange(historical1, historical2, current);
        await db.SaveChangesAsync();

        var count = await db.SceneImages.CountAsync(img => img.SessionId == sessionId && img.VisualRevision == 1);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task SecondCurrentArtifact_ForSameSessionAndRevision_ViolatesDatabaseConstraint()
    {
        await using var db = new CoreDbContext(_options);
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var current1 = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/cur1.png", "prompt", generationRequestId: Guid.NewGuid(), visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        db.SceneImages.Add(current1);
        await db.SaveChangesAsync();

        var current2 = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/cur2.png", "prompt", generationRequestId: Guid.NewGuid(), visualRevision: 1, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        db.SceneImages.Add(current2);

        // Assert: Database unique index on (SessionId, VisualRevision) WHERE IsCurrent = true throws DbUpdateException
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotNull(ex);
    }

    [Fact]
    public void PromoteArtifact_MonotonicallyAdvancesVisualRevision()
    {
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var initialArtifactId = Guid.NewGuid();
        var initialJobId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var state = new VisualSessionState(sessionId, initialArtifactId, initialJobId, visualRevision: 1, now);
        Assert.Equal(1, state.VisualRevision);

        // First promotion -> revision becomes 2
        var art2 = Guid.NewGuid();
        var job2 = Guid.NewGuid();
        var rev2 = state.PromoteArtifact(art2, job2, now.AddSeconds(1));
        Assert.Equal(2, rev2);
        Assert.Equal(2, state.VisualRevision);
        Assert.Equal(art2, state.CurrentImageId);

        // Second promotion -> revision becomes 3
        var art3 = Guid.NewGuid();
        var job3 = Guid.NewGuid();
        var rev3 = state.PromoteArtifact(art3, job3, now.AddSeconds(2));
        Assert.Equal(3, rev3);
        Assert.Equal(3, state.VisualRevision);
        Assert.Equal(art3, state.CurrentImageId);
    }
}
