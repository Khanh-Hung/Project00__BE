using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class VisualMemoryInvalidationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public VisualMemoryInvalidationTests()
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
    public async Task SupersededVisualMemory_IsExcludedFromReaderConditioning()
    {
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turn1 = Guid.NewGuid();
        var turn2 = Guid.NewGuid();

        await using (var db = new ProjectDbContext(_options))
        {
            // Seed Artifact 1 & Memory 1 (Red Dress)
            var art1 = new SceneImage(sessionId, charId, turn1, 1, "https://cdn.example.com/red_dress.png", "red dress");
            art1.PromoteToCurrent(1);
            await db.SceneImages.AddAsync(art1);

            var mem1 = new CharacterVisualMemory(charId, 1, 1, art1.Id, "Red Silk Dress", "outfit", 0.9f, 0.9f, 0.9f, turn1, turn1);
            await db.CharacterVisualMemories.AddAsync(mem1);

            // Seed Artifact 2 & Memory 2 (White Gown)
            var art2 = new SceneImage(sessionId, charId, turn2, 2, "https://cdn.example.com/white_gown.png", "white gown");
            art2.PromoteToCurrent(2);
            await db.SceneImages.AddAsync(art2);

            var mem2 = new CharacterVisualMemory(charId, 1, 2, art2.Id, "White Flowing Gown", "outfit", 0.95f, 0.95f, 0.95f, turn2, turn2);
            await db.CharacterVisualMemories.AddAsync(mem2);

            // Invalidate Memory 1 (superseded by turn 2)
            mem1.Invalidate(turn2);

            await db.SaveChangesAsync();
        }

        // Query through reader
        await using (var db = new ProjectDbContext(_options))
        {
            var reader = new VisualMemoryReader(db);
            var relevantMemories = await reader.GetRelevantMemoriesAsync(charId, maxResults: 5);

            // Assert: Only active Memory 2 (White Flowing Gown) is returned; Memory 1 is excluded
            Assert.Single(relevantMemories);
            Assert.Equal("White Flowing Gown", relevantMemories[0].Context);
            Assert.Null(relevantMemories[0].ValidUntilTurnId);
        }
    }
}
