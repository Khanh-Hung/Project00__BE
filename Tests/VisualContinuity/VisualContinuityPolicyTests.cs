using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class VisualContinuityPolicyTests
{
    [Fact]
    public void AuthorityHierarchy_CurrentIntent_OverridesAllLowerSources()
    {
        // Arrange
        var intentOutfit = "Silver Battle Armor";
        var prevOutfit = "Red Silk Robes";
        var memOutfit = "Academic Vestment";
        var defaultOutfit = "Canonical Attire";

        // Act
        var (resolvedOutfit, source) = VisualContinuityPolicy.ResolveOutfit(
            intentOutfit, prevOutfit, memOutfit, defaultOutfit);

        // Assert
        Assert.Equal("Silver Battle Armor", resolvedOutfit);
        Assert.Equal("CurrentIntent", source);
    }

    [Fact]
    public void AuthorityHierarchy_PreviousSceneState_WinsWhenIntentIsEmpty()
    {
        // Arrange
        string? intentOutfit = null;
        var prevOutfit = "Red Silk Robes";
        var memOutfit = "Academic Vestment";
        var defaultOutfit = "Canonical Attire";

        // Act
        var (resolvedOutfit, source) = VisualContinuityPolicy.ResolveOutfit(
            intentOutfit, prevOutfit, memOutfit, defaultOutfit);

        // Assert
        Assert.Equal("Red Silk Robes", resolvedOutfit);
        Assert.Equal("PreviousSceneState", source);
    }

    [Fact]
    public void AuthorityHierarchy_ActiveVisualMemory_WinsWhenIntentAndPreviousStateEmpty()
    {
        // Arrange
        string? intentOutfit = null;
        string? prevOutfit = null;
        var memOutfit = "Academic Vestment";
        var defaultOutfit = "Canonical Attire";

        // Act
        var (resolvedOutfit, source) = VisualContinuityPolicy.ResolveOutfit(
            intentOutfit, prevOutfit, memOutfit, defaultOutfit);

        // Assert
        Assert.Equal("Academic Vestment", resolvedOutfit);
        Assert.Equal("ActiveVisualMemory", source);
    }

    [Fact]
    public void AuthorityHierarchy_ProfileDefault_WinsWhenNoOtherSourcesExist()
    {
        // Arrange
        string? intentOutfit = null;
        string? prevOutfit = null;
        string? memOutfit = null;
        var defaultOutfit = "Canonical Attire";

        // Act
        var (resolvedOutfit, source) = VisualContinuityPolicy.ResolveOutfit(
            intentOutfit, prevOutfit, memOutfit, defaultOutfit);

        // Assert
        Assert.Equal("Canonical Attire", resolvedOutfit);
        Assert.Equal("ProfileDefault", source);
    }

    [Fact]
    public void ContradictoryOutfits_NeverMerge()
    {
        // Arrange: Previous outfit is Red Dress, current turn intent explicitly states White Gown
        var intentOutfit = "White Gown";
        var prevOutfit = "Red Dress";

        // Act
        var (resolvedOutfit, _) = VisualContinuityPolicy.ResolveOutfit(intentOutfit, prevOutfit, null, null);

        // Assert: Resolves cleanly to White Gown, not a merger like "Red and White Gown"
        Assert.Equal("White Gown", resolvedOutfit);
        Assert.DoesNotContain("Red", resolvedOutfit, StringComparison.OrdinalIgnoreCase);
    }
}
