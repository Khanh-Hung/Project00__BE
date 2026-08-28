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

public sealed class SceneReentryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public SceneReentryTests()
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
    public async Task SceneReentry_RestoresLatestValidState_OfPreviouslyVisitedScene()
    {
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turn1 = Guid.NewGuid();
        var turn2 = Guid.NewGuid();
        var turn3 = Guid.NewGuid();

        await using var db = new CoreDbContext(_options);
        var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
        var resolver = new VisualContinuityResolver(stateReader, NullLogger<VisualContinuityResolver>.Instance);

        // Turn 1: In Bedroom with specific lighting, weather, and world mutation
        var charState1 = new CharacterVisualState(charId, "Master Bedroom", 1, "Silk Nightgown");
        var bedroomState1 = new SceneVisualState(
            sessionId: sessionId,
            characterId: charId,
            location: "Master Bedroom",
            characterState: charState1,
            sceneRevision: 1,
            weather: "Thunderstorm",
            lighting: "dim candlelight",
            props: new[] { "Silver Candlestick", "Journal" },
            persistentChanges: new Dictionary<string, string> { ["BalconyDoor"] = "BoltedShut" },
            sourceTurnId: turn1
        );
        await stateReader.SaveStateAsync(bedroomState1);

        // Turn 2: Move to Kitchen (LocationTransition)
        var charState2 = new CharacterVisualState(charId, "Kitchen", 2, "Silk Nightgown");
        var kitchenState = new SceneVisualState(
            sessionId: sessionId,
            characterId: charId,
            location: "Kitchen",
            characterState: charState2,
            sceneRevision: 2,
            weather: "Clear",
            lighting: "warm hearth glow",
            sourceTurnId: turn2
        );
        await stateReader.SaveStateAsync(kitchenState);

        // Turn 3: Return to Master Bedroom (SceneReentry)
        var context = new SceneCompositionContext(
            CharacterId: charId,
            SessionId: sessionId,
            TurnId: turn3,
            SceneRevision: 3,
            PreviousScene: new SceneSpecification(charId, "Kitchen", "drinking water", 2, sessionId, turn2)
        );

        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Master Bedroom",
            actionHint: "returns to bed",
            sessionId: sessionId,
            turnId: turn3
        );

        // Act
        var result = await resolver.ResolveAsync(new VisualContinuityRequest(intent, context, 3));

        // Assert
        Assert.Equal(SceneTransitionType.SceneReentry, result.TransitionType);
        Assert.Equal("Master Bedroom", result.SceneVisualState.Location);
        Assert.Equal("Thunderstorm", result.SceneVisualState.Weather);
        Assert.Equal("dim candlelight", result.SceneVisualState.Lighting);
        Assert.Contains("Silver Candlestick", result.SceneVisualState.Props);
        Assert.Equal("BoltedShut", result.SceneVisualState.PersistentChanges["BalconyDoor"]);
    }
}
