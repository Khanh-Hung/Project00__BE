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
    private readonly DbContextOptions<CoreDbContext> _options;

    public VisualMemoryInvalidationTests()
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
    public async Task SupersededVisualMemory_IsExcludedFromReaderConditioning()
    {
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turn1 = Guid.NewGuid();
        var turn2 = Guid.NewGuid();

        await using (var db = new CoreDbContext(_options))
        {
            // Seed Artifact 1 & Memory 1 (Red Dress)
            var art1 = new SceneImage(sessionId, charId, turn1, 1, "https://cdn.example.com/red_dress.png", "red dress");
            art1.PromoteToCurrent(1);
            await db.SceneImages.AddAsync(art1);

            var mem1 = new CharacterVisualMemory(
                characterId: charId,
                visualProfileVersion: 1,
                sceneRevision: 1,
                artifactId: art1.Id,
                context: "Red Silk Dress",
                tags: "outfit",
                outfit: "Red Silk Dress",
                qualityScore: 0.9f,
                identityScore: 0.9f,
                featureScore: 0.9f,
                sourceTurnId: turn1,
                validFromTurnId: turn1,
                validFromRevision: 1
            );
            await db.CharacterVisualMemories.AddAsync(mem1);

            // Seed Artifact 2 & Memory 2 (White Gown)
            var art2 = new SceneImage(sessionId, charId, turn2, 2, "https://cdn.example.com/white_gown.png", "white gown");
            art2.PromoteToCurrent(2);
            await db.SceneImages.AddAsync(art2);

            var mem2 = new CharacterVisualMemory(
                characterId: charId,
                visualProfileVersion: 1,
                sceneRevision: 2,
                artifactId: art2.Id,
                context: "White Flowing Gown",
                tags: "outfit",
                outfit: "White Flowing Gown",
                qualityScore: 0.95f,
                identityScore: 0.95f,
                featureScore: 0.95f,
                sourceTurnId: turn2,
                validFromTurnId: turn2,
                validFromRevision: 2
            );
            await db.CharacterVisualMemories.AddAsync(mem2);

            // Invalidate Memory 1 (superseded starting at revision 2)
            mem1.Invalidate(turn2, supersededByRevision: 2);

            await db.SaveChangesAsync();
        }

        // Query through reader
        await using (var db = new CoreDbContext(_options))
        {
            var reader = new VisualMemoryReader(db);
            var relevantMemories = await reader.GetRelevantMemoriesAsync(charId, maxResults: 5);

            // Assert: Only active Memory 2 (White Flowing Gown) is returned; Memory 1 is excluded
            Assert.Single(relevantMemories);
            Assert.Equal("White Flowing Gown", relevantMemories[0].Context);
            Assert.Equal("White Flowing Gown", relevantMemories[0].Outfit);
            Assert.Null(relevantMemories[0].ValidUntilTurnId);
        }
    }
}
