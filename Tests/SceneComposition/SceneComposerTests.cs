using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneComposerTests
{
    [Fact]
    public async Task ComposeAsync_WithRawIntent_NormalizesAndResolvesDefaults()
    {
        var composer = new SceneComposer(NullLogger<SceneComposer>.Instance);
        var charId = Guid.NewGuid();

        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Royal Library",
            actionHint: "Reading an ancient scroll"
        );

        var context = new SceneCompositionContext(
            CharacterId: charId,
            SceneRevision: 1
        );

        var spec = await composer.ComposeAsync(intent, context);

        Assert.NotNull(spec);
        Assert.Equal(charId, spec.CharacterId);
        Assert.Equal("Royal Library", spec.Location);
        Assert.Equal("Reading an ancient scroll", spec.Action);
        Assert.Equal("seated naturally", spec.Pose); // Inferred from "reading"
        Assert.Equal("ambient interior lighting mixed with soft window daylight", spec.Lighting); // Inferred from indoor daytime
        Assert.Equal("clear", spec.Weather);
        Assert.Equal("daytime", spec.TimeOfDay);
        Assert.Equal("neutral cinematic", spec.Mood);
    }

    [Fact]
    public async Task ComposeAsync_WhenCharacterMismatch_ThrowsArgumentException()
    {
        var composer = new SceneComposer(NullLogger<SceneComposer>.Instance);
        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();

        var intent = new SceneIntent(charA, "Library", "Reading");
        var context = new SceneCompositionContext(CharacterId: charB);

        await Assert.ThrowsAsync<ArgumentException>(() => composer.ComposeAsync(intent, context));
    }

    [Fact]
    public async Task ComposeAsync_PreservesCharacterIdentityBoundaries_DoesNotMutateContext()
    {
        var composer = new SceneComposer(NullLogger<SceneComposer>.Instance);
        var charId = Guid.NewGuid();

        var profile = new CharacterVisualProfile(
            characterId: charId,
            eyeColor: "Crimson Red",
            hairColor: "Silver",
            currentOutfit: "Imperial Battle Robes"
        );

        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Dark Dungeon",
            actionHint: "Casting a spell",
            outfitHint: "Tattered Cloak"
        );

        var context = new SceneCompositionContext(
            CharacterId: charId,
            CharacterVisualProfile: profile,
            SceneRevision: 2
        );

        var spec = await composer.ComposeAsync(intent, context);

        Assert.Equal("Tattered Cloak", spec.OutfitContext);

        // Invariant: Character profile is strictly immutable from scene composition
        Assert.Equal("Imperial Battle Robes", profile.CurrentOutfit);
        Assert.Equal("Crimson Red", profile.EyeColor);
        Assert.Equal("Silver", profile.HairColor);
    }
}
