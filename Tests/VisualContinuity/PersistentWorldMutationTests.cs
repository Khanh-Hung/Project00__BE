using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class PersistentWorldMutationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public PersistentWorldMutationTests()
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
    public void SceneVisualState_ApplyWorldMutation_UpdatesStateAndFingerprint()
    {
        // Arrange
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var charState = new CharacterVisualState(charId, "Tavern Dining Room", 1);
        var sceneState = new SceneVisualState(
            sessionId: sessionId,
            characterId: charId,
            location: "Tavern Dining Room",
            characterState: charState,
            sceneRevision: 1
        );

        var initialFingerprint = sceneState.Fingerprint;

        // Act: Apply mutation "Glass: BrokenOnFloor" and "Door: KickedOpen"
        sceneState.ApplyWorldMutation("Glass", "BrokenOnFloor", turnId, revision: 1);
        sceneState.ApplyWorldMutation("FrontDoor", "KickedOpen", turnId, revision: 1);

        // Assert
        Assert.Equal("BrokenOnFloor", sceneState.PersistentChanges["Glass"]);
        Assert.Equal("KickedOpen", sceneState.PersistentChanges["FrontDoor"]);
        Assert.NotEqual(initialFingerprint, sceneState.Fingerprint);
        Assert.True(sceneState.Version > 1);
    }

    [Fact]
    public async Task SameSceneEvolution_PreservesWorldMutationsAcrossTurns()
    {
        // Arrange
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turn1 = Guid.NewGuid();
        var turn2 = Guid.NewGuid();

        await using var db = new CoreDbContext(_options);
        var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
        var resolver = new VisualContinuityResolver(stateReader, NullLogger<VisualContinuityResolver>.Instance);

        var charState1 = new CharacterVisualState(charId, "Alchemist Workshop", 1);
        var sceneState1 = new SceneVisualState(
            sessionId: sessionId,
            characterId: charId,
            location: "Alchemist Workshop",
            characterState: charState1,
            sceneRevision: 1,
            persistentChanges: new Dictionary<string, string>
            {
                ["CrystalFlask"] = "ShatteredOnWorkbench",
                ["Furnace"] = "BlazingBlueFlames"
            },
            sourceTurnId: turn1
        );
        await stateReader.SaveStateAsync(sceneState1);

        var prevSpec = new SceneSpecification(
            characterId: charId,
            location: "Alchemist Workshop",
            action: "knocked over the flask",
            sceneRevision: 1,
            sessionId: sessionId,
            turnId: turn1
        );

        var context = new SceneCompositionContext(
            CharacterId: charId,
            SessionId: sessionId,
            TurnId: turn2,
            SceneRevision: 2,
            PreviousScene: prevSpec
        );

        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Alchemist Workshop",
            actionHint: "examines the glowing residue",
            sessionId: sessionId,
            turnId: turn2
        );

        // Act
        var result = await resolver.ResolveAsync(new VisualContinuityRequest(intent, context, 2));

        // Assert: SameScene transition preserves world mutations
        Assert.Equal(SceneTransitionType.SameScene, result.TransitionType);
        Assert.Equal("ShatteredOnWorkbench", result.SceneVisualState.PersistentChanges["CrystalFlask"]);
        Assert.Equal("BlazingBlueFlames", result.SceneVisualState.PersistentChanges["Furnace"]);
    }
}
