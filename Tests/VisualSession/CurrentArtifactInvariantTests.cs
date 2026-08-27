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
    private readonly DbContextOptions<ProjectDbContext> _options;

    public CurrentArtifactInvariantTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new ProjectDbContext(_options);
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
        await using var db = new ProjectDbContext(_options);
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
        await using var db = new ProjectDbContext(_options);
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
        await using var db = new ProjectDbContext(_options);
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
}
