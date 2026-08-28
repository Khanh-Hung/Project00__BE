using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class VisualHistoryTests
{
    [Fact]
    public async Task GetSessionVisualHistory_ReturnsNewestToOldest_AndSingleCurrentArtifact()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new CoreDbContext(options);
        var service = new VisualHistoryService(db, NullLogger<VisualHistoryService>.Instance);

        var sessionId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        var t0 = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc);

        // Turn 1 artifact (Historical)
        var img1 = new SceneImage(sessionId, characterId, Guid.NewGuid(), 1, "https://cdn.project00.ai/img1.png", "prompt 1", visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Historical);
        img1.SetCreated(t0.AddMinutes(1));
        await db.SceneImages.AddAsync(img1);

        // Turn 2 artifact (Quarantined)
        var img2 = new SceneImage(sessionId, characterId, Guid.NewGuid(), 2, "https://cdn.project00.ai/img2.png", "prompt 2", visualRevision: 1, isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Quarantined);
        img2.SetCreated(t0.AddMinutes(2));
        await db.SceneImages.AddAsync(img2);

        // Turn 3 artifact (Current)
        var img3 = new SceneImage(sessionId, characterId, Guid.NewGuid(), 3, "https://cdn.project00.ai/img3.png", "prompt 3", visualRevision: 2, isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);
        img3.SetCreated(t0.AddMinutes(3));
        await db.SceneImages.AddAsync(img3);

        await db.SaveChangesAsync();

        var history = await service.GetSessionVisualHistoryAsync(sessionId);

        Assert.Equal(3, history.Count);

        // Check ordering: img3 -> img2 -> img1
        Assert.Equal(img3.Id, history[0].ArtifactId);
        Assert.True(history[0].IsCurrent);
        Assert.Equal("Current", history[0].LifecycleStatus);

        Assert.Equal(img2.Id, history[1].ArtifactId);
        Assert.False(history[1].IsCurrent);
        Assert.True(history[1].IsQuarantined);
        Assert.Equal("Quarantined", history[1].LifecycleStatus);

        Assert.Equal(img1.Id, history[2].ArtifactId);
        Assert.False(history[2].IsCurrent);
        Assert.Equal("Historical", history[2].LifecycleStatus);

        // Single current artifact invariant
        Assert.Single(history, h => h.IsCurrent);
    }

    [Fact]
    public async Task GetSessionVisualHistory_RespectsLimitParameter()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new CoreDbContext(options);
        var service = new VisualHistoryService(db, NullLogger<VisualHistoryService>.Instance);

        var sessionId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        for (int i = 1; i <= 10; i++)
        {
            var img = new SceneImage(sessionId, characterId, Guid.NewGuid(), i, $"https://cdn.project00.ai/img{i}.png", $"prompt {i}", visualRevision: i, isCurrent: i == 10);
            img.SetCreated(DateTime.UtcNow.AddMinutes(i));
            await db.SceneImages.AddAsync(img);
        }
        await db.SaveChangesAsync();

        var history = await service.GetSessionVisualHistoryAsync(sessionId, limit: 3);

        Assert.Equal(3, history.Count);
    }
}
