using Domain.Entities;
using Xunit;

namespace Tests.VisualIdentity;

public sealed class CharacterVisualProfileTests
{
    [Fact]
    public void CreateProfile_WithInitialTraits_InitializesAtVersionOne()
    {
        var charId = Guid.NewGuid();
        var profile = new CharacterVisualProfile(
            characterId: charId,
            hairDescription: "Silver hair, medium length",
            eyeDescription: "Crimson red eyes",
            skinDescription: "Pale fair skin",
            bodyDescription: "Slender athletic build",
            distinguishingFeatures: "Small crescent scar below left eye"
        );

        Assert.Equal(charId, profile.CharacterId);
        Assert.Equal(1, profile.VisualVersion);
        Assert.Equal("Silver hair, medium length", profile.HairDescription);
        Assert.Equal("Crimson red eyes", profile.EyeDescription);
        Assert.Equal("Pale fair skin", profile.SkinDescription);
        Assert.Equal("Slender athletic build", profile.BodyDescription);
        Assert.Equal("Small crescent scar below left eye", profile.DistinguishingFeatures);
        Assert.Null(profile.PrimaryReferenceId);
        Assert.Null(profile.FaceReferenceId);
    }

    [Fact]
    public void CreateProfile_WithEmptyCharacterId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CharacterVisualProfile(Guid.Empty));
    }

    [Fact]
    public void UpdateAppearance_MonotonicallyAdvancesVisualVersion()
    {
        var charId = Guid.NewGuid();
        var profile = new CharacterVisualProfile(charId, "Black hair", "Brown eyes");
        Assert.Equal(1, profile.VisualVersion);

        var now = DateTime.UtcNow;
        profile.UpdateAppearance("Midnight blue hair", "Glowing blue eyes", "Pale", "Tall", "None", now);
        Assert.Equal(2, profile.VisualVersion);
        Assert.Equal("Midnight blue hair", profile.HairDescription);
        Assert.Equal("Glowing blue eyes", profile.EyeDescription);

        profile.UpdateAppearance("Golden blonde hair", "Glowing blue eyes", "Pale", "Tall", "None", now.AddSeconds(1));
        Assert.Equal(3, profile.VisualVersion);
        Assert.Equal("Golden blonde hair", profile.HairDescription);
    }

    [Fact]
    public void PromoteReferenceToCanonical_MonotonicallyAdvancesVisualVersionAndSetsPointers()
    {
        var charId = Guid.NewGuid();
        var profile = new CharacterVisualProfile(charId);
        Assert.Equal(1, profile.VisualVersion);

        var refId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        profile.PromoteReferenceToCanonical(refId, isFaceOnly: false, now);

        Assert.Equal(2, profile.VisualVersion);
        Assert.Equal(refId, profile.PrimaryReferenceId);
        Assert.Equal(refId, profile.FaceReferenceId);

        var faceRefId = Guid.NewGuid();
        profile.PromoteReferenceToCanonical(faceRefId, isFaceOnly: true, now.AddSeconds(1));

        Assert.Equal(3, profile.VisualVersion);
        Assert.Equal(refId, profile.PrimaryReferenceId);
        Assert.Equal(faceRefId, profile.FaceReferenceId);
    }
}
