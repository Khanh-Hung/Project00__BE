using Domain.ValueObjects;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneEnvironmentTests
{
    [Fact]
    public void SceneEnvironment_InitializesCorrectly_WithBackgroundAndProps()
    {
        var env = new SceneEnvironment(
            location: "Gothic Library",
            architecture: "High vaulted stone arches with stained glass",
            backgroundElements: new[] { "Bookshelves", "Tall windows" },
            foregroundElements: new[] { "Carved oak desk", "Velvet chair" },
            props: new[] { "Ancient Tome", "Silver Candleholder" },
            weather: "Rain",
            timeOfDay: "Night",
            lighting: "Flickering candlelight",
            atmosphere: "Quiet, melancholic"
        );

        Assert.Equal("Gothic Library", env.Location);
        Assert.Equal("High vaulted stone arches with stained glass", env.Architecture);
        Assert.Equal(2, env.BackgroundElements.Length);
        Assert.Equal(2, env.ForegroundElements.Length);
        Assert.Equal(2, env.Props.Length);
        Assert.Equal("Rain", env.Weather);
        Assert.Equal("Night", env.TimeOfDay);
        Assert.Equal("Flickering candlelight", env.Lighting);
        Assert.Equal("Quiet, melancholic", env.Atmosphere);
    }

    [Fact]
    public void SceneEnvironment_WithEmptyLocation_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new SceneEnvironment("  "));
    }
}
