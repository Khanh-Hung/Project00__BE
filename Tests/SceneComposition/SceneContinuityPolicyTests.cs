using Domain.Enums;
using Domain.Policies;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneContinuityPolicyTests
{
    [Fact]
    public void EvaluateTransition_WhenExactLocationMatches_ReturnsSameScene()
    {
        var result = SceneContinuityPolicy.EvaluateTransition(
            previousLocation: "Gothic Library",
            currentLocation: "Gothic Library",
            previousAction: "reading a book",
            currentAction: "turning the page"
        );

        Assert.Equal(SceneTransitionType.SameScene, result);
    }

    [Fact]
    public void EvaluateTransition_WhenStructuredSubLocationMatches_ReturnsSameLocation()
    {
        var result = SceneContinuityPolicy.EvaluateTransition(
            previousLocation: "Grand Palace - Throne Room",
            currentLocation: "Grand Palace - Courtyard",
            previousAction: "walking into the palace",
            currentAction: "kneeling before throne"
        );

        Assert.Equal(SceneTransitionType.SameLocation, result);
    }

    [Theory]
    [InlineData("Forest", "Rainforest")]
    [InlineData("City", "Old City")]
    [InlineData("Library", "Balcony Garden")]
    [InlineData("Bedroom", "Bedroom dream sequence")]
    public void EvaluateTransition_WhenUnrelatedOrSubstringLocations_ReturnsLocationTransition_NoHeuristicFalsePositives(
        string prevLocation,
        string currLocation)
    {
        var result = SceneContinuityPolicy.EvaluateTransition(
            previousLocation: prevLocation,
            currentLocation: currLocation,
            previousAction: "action a",
            currentAction: "action b"
        );

        // Invariant: Substring matching is strictly rejected; returns LocationTransition
        Assert.Equal(SceneTransitionType.LocationTransition, result);
    }
}
