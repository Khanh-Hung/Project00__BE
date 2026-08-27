using Domain.Entities;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneIntentTests
{
    [Fact]
    public void Constructor_WithValidInputs_InitializesCorrectly()
    {
        var charId = Guid.NewGuid();
        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Grand Ballroom",
            actionHint: "Dancing a waltz",
            poseHint: "in dance hold",
            lightingHint: "chandelier sparkle",
            objectHints: new[] { "Chandelier", "Wine Glass" }
        );

        Assert.Equal(charId, intent.CharacterId);
        Assert.Equal("Grand Ballroom", intent.LocationHint);
        Assert.Equal("Dancing a waltz", intent.ActionHint);
        Assert.Equal("in dance hold", intent.PoseHint);
        Assert.Equal(2, intent.ObjectHints.Count);
    }

    [Fact]
    public void Constructor_WithEmptyLocationOrAction_ThrowsArgumentException()
    {
        var charId = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new SceneIntent(charId, "", "Action"));
        Assert.Throws<ArgumentException>(() => new SceneIntent(charId, "Location", ""));
        Assert.Throws<ArgumentException>(() => new SceneIntent(Guid.Empty, "Location", "Action"));
    }
}
