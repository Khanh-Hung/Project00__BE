using Domain.Entities;
using Infrastructure.Persistence;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.AutonomousLoop;

public sealed class SceneRevisionOrderingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public SceneRevisionOrderingTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new ProjectDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task OlderSceneRevision_CannotOverwrite_NewerAuthoritativeSceneRevision()
    {
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var sceneKey = "throne-room";

        // 1. Revision 2 (N+1) finishes first and writes to DB
        var charState2 = new CharacterVisualState(charId, "Throne Room - Night", sceneRevision: 2, outfit: "Regal Gown");
        var stateRev2 = new SceneVisualState(
            sessionId: sessionId,
            characterId: charId,
            location: "Throne Room - Night",
            characterState: charState2,
            sceneRevision: 2,
            sceneKey: sceneKey,
            sourceTurnId: Guid.NewGuid()
        );

        using (var db = new ProjectDbContext(_options))
        {
            var reader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            await reader.SaveStateAsync(stateRev2);
        }

        // 2. Revision 1 (N) arrives late (out-of-order background execution) and attempts to write
        var charState1 = new CharacterVisualState(charId, "Throne Room - Day", sceneRevision: 1, outfit: "Travel Cloak");
        var stateRev1 = new SceneVisualState(
            sessionId: sessionId,
            characterId: charId,
            location: "Throne Room - Day",
            characterState: charState1,
            sceneRevision: 1,
            sceneKey: sceneKey,
            sourceTurnId: Guid.NewGuid()
        );

        using (var db = new ProjectDbContext(_options))
        {
            var reader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            await reader.SaveStateAsync(stateRev1);
        }

        // 3. Assert: Authoritative DB State remains Revision 2!
        using (var db = new ProjectDbContext(_options))
        {
            var reader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var latest = await reader.GetLatestBySessionAndSceneKeyAsync(sessionId, sceneKey);

            Assert.NotNull(latest);
            Assert.Equal(2, latest.SceneRevision);
            Assert.Equal("Throne Room - Night", latest.Location);
            Assert.Equal("Regal Gown", latest.CharacterState.Outfit);
        }
    }
}
