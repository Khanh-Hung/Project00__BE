using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneDeterminismTests
{
    [Fact]
    public async Task Compose_WithSameInputs_ProducesIdenticalOutputs()
    {
        var composer = new SceneComposer(NullLogger<SceneComposer>.Instance);
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var intent1 = new SceneIntent(
            characterId: charId,
            locationHint: "Mystic Forest",
            actionHint: "Searching for herbs",
            sessionId: sessionId,
            turnId: turnId
        );

        var intent2 = new SceneIntent(
            characterId: charId,
            locationHint: "Mystic Forest",
            actionHint: "Searching for herbs",
            sessionId: sessionId,
            turnId: turnId
        );

        var context1 = new SceneCompositionContext(CharacterId: charId, SessionId: sessionId, TurnId: turnId, SceneRevision: 1);
        var context2 = new SceneCompositionContext(CharacterId: charId, SessionId: sessionId, TurnId: turnId, SceneRevision: 1);

        var spec1 = await composer.ComposeAsync(intent1, context1);
        var spec2 = await composer.ComposeAsync(intent2, context2);

        Assert.Equal(spec1.Location, spec2.Location);
        Assert.Equal(spec1.Action, spec2.Action);
        Assert.Equal(spec1.Pose, spec2.Pose);
        Assert.Equal(spec1.Lighting, spec2.Lighting);
        Assert.Equal(spec1.Camera, spec2.Camera);
        Assert.Equal(spec1.Weather, spec2.Weather);
        Assert.Equal(spec1.TimeOfDay, spec2.TimeOfDay);
        Assert.Equal(spec1.Mood, spec2.Mood);
    }
}
