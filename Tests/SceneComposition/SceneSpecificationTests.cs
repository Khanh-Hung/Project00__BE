using Domain.Entities;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneSpecificationTests
{
    [Fact]
    public void Constructor_WithValidInputs_InitializesCorrectly()
    {
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var spec = new SceneSpecification(
            characterId: charId,
            location: "Gothic Library",
            action: "Reading an ancient grimoire",
            sceneRevision: 1,
            sessionId: sessionId,
            turnId: turnId,
            pose: "seated at desk",
            environment: "Tall bookshelves, dusty windows",
            lighting: "warm candlelight",
            camera: "medium close-up",
            weather: "rain outside",
            timeOfDay: "night",
            mood: "mysterious",
            outfitContext: "Scholar robes",
            objects: new[] { "Grimoire", "Candle", "Inkwell" },
            atmosphereElements: new[] { "Dust motes", "Shadows" },
            now: now
        );

        Assert.NotEqual(Guid.Empty, spec.Id);
        Assert.Equal(charId, spec.CharacterId);
        Assert.Equal("Gothic Library", spec.Location);
        Assert.Equal("Reading an ancient grimoire", spec.Action);
        Assert.Equal(1, spec.SceneRevision);
        Assert.Equal(sessionId, spec.SessionId);
        Assert.Equal(turnId, spec.TurnId);
        Assert.Equal("seated at desk", spec.Pose);
        Assert.Equal("Tall bookshelves, dusty windows", spec.Environment);
        Assert.Equal(3, spec.Objects.Count);
        Assert.Equal(2, spec.AtmosphereElements.Count);
        Assert.Equal(now, spec.CreatedAt);
    }

    [Fact]
    public void Constructor_WithEmptyCharacterId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new SceneSpecification(
            characterId: Guid.Empty,
            location: "Library",
            action: "Reading"
        ));
    }

    [Fact]
    public void Constructor_WithEmptyLocation_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new SceneSpecification(
            characterId: Guid.NewGuid(),
            location: "   ",
            action: "Reading"
        ));
    }

    [Fact]
    public void Constructor_WithEmptyAction_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new SceneSpecification(
            characterId: Guid.NewGuid(),
            location: "Library",
            action: ""
        ));
    }

    [Fact]
    public void Constructor_WithInvalidSceneRevision_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneSpecification(
            characterId: Guid.NewGuid(),
            location: "Library",
            action: "Reading",
            sceneRevision: 0
        ));
    }
}
