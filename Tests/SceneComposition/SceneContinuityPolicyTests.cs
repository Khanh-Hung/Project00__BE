using Domain.Enums;
using Domain.Policies;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneContinuityPolicyTests
{
    [Fact]
    public void EvaluateTransition_WhenLocationMatches_ReturnsSameScene()
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
    public void EvaluateTransition_WhenLocationIsSubcategory_ReturnsSameLocation()
    {
        var result = SceneContinuityPolicy.EvaluateTransition(
            previousLocation: "Grand Palace",
            currentLocation: "Grand Palace - Throne Room",
            previousAction: "walking into the palace",
            currentAction: "kneeling before throne"
        );

        Assert.Equal(SceneTransitionType.SameLocation, result);
    }

    [Fact]
    public void EvaluateTransition_WhenLocationCompletelyChanges_ReturnsLocationTransition()
    {
        var result = SceneContinuityPolicy.EvaluateTransition(
            previousLocation: "Library",
            currentLocation: "Balcony Garden",
            previousAction: "reading",
            currentAction: "looking at stars"
        );

        Assert.Equal(SceneTransitionType.LocationTransition, result);
    }
}
