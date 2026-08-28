using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneDeterminismTests
{
    [Fact]
    public async Task Compose_WithSameInputs_ProducesIdenticalOutputsAndFingerprint()
    {
        var composer = new SceneComposer(NullLogger<SceneComposer>.Instance);
        var promptComposer = new ScenePromptComposer();
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var intent1 = new SceneIntent(
            characterId: charId,
            locationHint: "Mystic Forest",
            actionHint: "Searching for herbs",
            sessionId: sessionId,
            turnId: turnId,
            environmentHint: "Ancient mossy trees",
            objectHints: new[] { "Herbal Pouch", "Staff" }
        );

        var intent2 = new SceneIntent(
            characterId: charId,
            locationHint: "Mystic Forest",
            actionHint: "Searching for herbs",
            sessionId: sessionId,
            turnId: turnId,
            environmentHint: "Ancient mossy trees",
            objectHints: new[] { "Herbal Pouch", "Staff" }
        );

        var context1 = new SceneCompositionContext(CharacterId: charId, SessionId: sessionId, TurnId: turnId, SceneRevision: 1);
        var context2 = new SceneCompositionContext(CharacterId: charId, SessionId: sessionId, TurnId: turnId, SceneRevision: 1);

        var spec1 = await composer.ComposeAsync(intent1, context1);
        var spec2 = await composer.ComposeAsync(intent2, context2);

        // Assert deterministic fields
        Assert.Equal(spec1.Location, spec2.Location);
        Assert.Equal(spec1.Action, spec2.Action);
        Assert.Equal(spec1.Pose, spec2.Pose);
        Assert.Equal(spec1.Lighting, spec2.Lighting);
        Assert.Equal(spec1.Camera, spec2.Camera);
        Assert.Equal(spec1.Weather, spec2.Weather);
        Assert.Equal(spec1.TimeOfDay, spec2.TimeOfDay);
        Assert.Equal(spec1.Mood, spec2.Mood);

        // Assert deterministic Content Fingerprint
        Assert.Equal(spec1.SceneFingerprint, spec2.SceneFingerprint);

        // Assert deterministic prompt compilation
        var vContext = new VisualContextResolutionResult(
            CharacterId: charId,
            VisualProfileVersion: 1,
            CanonicalIdentityReference: null,
            CurrentAppearance: null,
            PredecessorVisualMemory: null,
            RelevantOlderMemories: Array.Empty<CharacterVisualMemory>(),
            TransitionType: Domain.Enums.SceneTransitionType.LocationTransition,
            SelectionSummary: "Determinism test"
        );

        var prompt1 = promptComposer.ComposePrompt(spec1, vContext);
        var prompt2 = promptComposer.ComposePrompt(spec2, vContext);

        Assert.Equal(prompt1.PositivePrompt, prompt2.PositivePrompt);
        Assert.Equal(prompt1.NegativePrompt, prompt2.NegativePrompt);
        Assert.Equal(prompt1.StructuredSummary, prompt2.StructuredSummary);
    }
}
