using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.SceneComposition;

public sealed class ScenePromptComposerTests
{
    [Fact]
    public void ComposePrompt_ProducesStructuredSections_Deterministically()
    {
        var composer = new ScenePromptComposer();
        var charId = Guid.NewGuid();

        var profile = new CharacterVisualProfile(
            characterId: charId,
            eyeColor: "Crimson Red",
            hairColor: "Silver",
            currentOutfit: "Ebony Armor"
        );

        var spec = new SceneSpecification(
            characterId: charId,
            location: "Ancient Library",
            action: "Reading a spellbook",
            pose: "seated at desk",
            environment: "Tall bookshelves with ancient tomes",
            lighting: "warm candlelight",
            camera: "medium cinematic shot",
            weather: "stormy rain",
            timeOfDay: "midnight",
            mood: "tense atmosphere",
            outfitContext: "Ebony Armor",
            objects: new[] { "Spellbook", "Candle" }
        );

        var visualContext = new VisualContextResolutionResult(
            CharacterId: charId,
            VisualProfileVersion: 1,
            CanonicalIdentityReference: null,
            CurrentAppearance: profile,
            PredecessorVisualMemory: null,
            RelevantOlderMemories: Array.Empty<CharacterVisualMemory>(),
            TransitionType: SceneTransitionType.SameScene,
            SelectionSummary: "Test Summary"
        );

        var promptA = composer.ComposePrompt(spec, visualContext);
        var promptB = composer.ComposePrompt(spec, visualContext);

        Assert.Equal(promptA.PositivePrompt, promptB.PositivePrompt);
        Assert.Equal(promptA.NegativePrompt, promptB.NegativePrompt);
        Assert.Equal(promptA.StructuredSummary, promptB.StructuredSummary);

        // Verify key sections are present
        Assert.Contains("[Character:", promptA.PositivePrompt);
        Assert.Contains("Silver hair, Crimson Red eyes", promptA.PositivePrompt);
        Assert.Contains("[Action: Reading a spellbook]", promptA.PositivePrompt);
        Assert.Contains("[Pose: seated at desk]", promptA.PositivePrompt);
        Assert.Contains("[Outfit: Ebony Armor]", promptA.PositivePrompt);
        Assert.Contains("[Environment: Tall bookshelves with ancient tomes]", promptA.PositivePrompt);
        Assert.Contains("[Props: Spellbook, Candle]", promptA.PositivePrompt);
        Assert.Contains("[Camera: medium cinematic shot]", promptA.PositivePrompt);
        Assert.Contains("[Lighting: warm candlelight]", promptA.PositivePrompt);
        Assert.Contains("[Weather: stormy rain]", promptA.PositivePrompt);
        Assert.Contains("[Time: midnight]", promptA.PositivePrompt);
        Assert.Contains("[Mood: tense atmosphere]", promptA.PositivePrompt);
        Assert.Contains("[Continuity: seamless visual continuation of previous scene]", promptA.PositivePrompt);
    }
}
