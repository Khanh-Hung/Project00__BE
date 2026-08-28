using Domain.Entities;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class VisualContinuityFingerprintTests
{
    [Fact]
    public void SameLogicalState_ProducesIdenticalFingerprint()
    {
        var charId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var props = new[] { "Ancient Tome", "Quill", "Inkpot" };
        var changes = new Dictionary<string, string>
        {
            ["Window"] = "Open",
            ["Candle"] = "Lit"
        };

        var fp1 = SceneVisualState.ComputeFingerprint(
            charId, "Grand Library", "Scholar Robes", "Braided", "Sitting", "Reading",
            "Evening", "Rainy", "Warm Candlelight", "Scholarly", props, changes, 1);

        var fp2 = SceneVisualState.ComputeFingerprint(
            charId, "Grand Library", "Scholar Robes", "Braided", "Sitting", "Reading",
            "Evening", "Rainy", "Warm Candlelight", "Scholarly", props, changes, 1);

        Assert.Equal(fp1, fp2);
        Assert.Equal(64, fp1.Length);
    }

    [Fact]
    public void DifferentPropsOrdering_ProducesIdenticalFingerprint_DueToCanonicalSorting()
    {
        var charId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var props1 = new[] { "Ancient Tome", "Quill", "Inkpot" };
        var props2 = new[] { "Inkpot", "Ancient Tome", "Quill" }; // Shuffled

        var changes = new Dictionary<string, string> { ["Door"] = "Closed" };

        var fp1 = SceneVisualState.ComputeFingerprint(
            charId, "Grand Library", "Scholar Robes", "Braided", "Sitting", "Reading",
            "Evening", "Rainy", "Warm Candlelight", "Scholarly", props1, changes, 1);

        var fp2 = SceneVisualState.ComputeFingerprint(
            charId, "Grand Library", "Scholar Robes", "Braided", "Sitting", "Reading",
            "Evening", "Rainy", "Warm Candlelight", "Scholarly", props2, changes, 1);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void DifferentPersistentChangesOrdering_ProducesIdenticalFingerprint()
    {
        var charId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var props = new[] { "Tome" };

        var changes1 = new Dictionary<string, string>
        {
            ["A_Item"] = "State1",
            ["B_Item"] = "State2"
        };

        var changes2 = new Dictionary<string, string>
        {
            ["B_Item"] = "State2",
            ["A_Item"] = "State1" // Inverted order
        };

        var fp1 = SceneVisualState.ComputeFingerprint(
            charId, "Grand Library", "Scholar Robes", "Braided", "Sitting", "Reading",
            "Evening", "Rainy", "Warm Candlelight", "Scholarly", props, changes1, 1);

        var fp2 = SceneVisualState.ComputeFingerprint(
            charId, "Grand Library", "Scholar Robes", "Braided", "Sitting", "Reading",
            "Evening", "Rainy", "Warm Candlelight", "Scholarly", props, changes2, 1);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void MutatingSingleField_AltersFingerprint()
    {
        var charId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var props = new[] { "Tome" };
        var changes = new Dictionary<string, string> { ["Door"] = "Closed" };

        var baseFp = SceneVisualState.ComputeFingerprint(
            charId, "Grand Library", "Scholar Robes", "Braided", "Sitting", "Reading",
            "Evening", "Rainy", "Warm Candlelight", "Scholarly", props, changes, 1);

        var differentOutfitFp = SceneVisualState.ComputeFingerprint(
            charId, "Grand Library", "Battle Armor", "Braided", "Sitting", "Reading",
            "Evening", "Rainy", "Warm Candlelight", "Scholarly", props, changes, 1);

        var differentWeatherFp = SceneVisualState.ComputeFingerprint(
            charId, "Grand Library", "Scholar Robes", "Braided", "Sitting", "Reading",
            "Evening", "Sunny", "Warm Candlelight", "Scholarly", props, changes, 1);

        Assert.NotEqual(baseFp, differentOutfitFp);
        Assert.NotEqual(baseFp, differentWeatherFp);
    }
}
