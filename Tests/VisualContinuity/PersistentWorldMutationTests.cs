using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class PersistentWorldMutationTests
{
    [Fact]
    public void SceneVisualState_ApplyWorldMutation_UpdatesStateAndFingerprint()
    {
        // Arrange
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var charState = new CharacterVisualState(charId, "Tavern Dining Room", 1);
        var sceneState = new SceneVisualState(
            sessionId: sessionId,
            characterId: charId,
            location: "Tavern Dining Room",
            characterState: charState,
            sceneRevision: 1
        );

        var initialFingerprint = sceneState.Fingerprint;

        // Act: Apply mutation "Glass: BrokenOnFloor" and "Door: KickedOpen"
        sceneState.ApplyWorldMutation("Glass", "BrokenOnFloor", turnId);
        sceneState.ApplyWorldMutation("FrontDoor", "KickedOpen", turnId);

        // Assert
        Assert.Equal("BrokenOnFloor", sceneState.PersistentChanges["Glass"]);
        Assert.Equal("KickedOpen", sceneState.PersistentChanges["FrontDoor"]);
        Assert.NotEqual(initialFingerprint, sceneState.Fingerprint);
        Assert.True(sceneState.Version > 1);
    }

    [Fact]
    public async Task SameSceneEvolution_PreservesWorldMutationsAcrossTurns()
    {
        // Arrange
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turn1 = Guid.NewGuid();
        var turn2 = Guid.NewGuid();

        var resolver = new VisualContinuityResolver(NullLogger<VisualContinuityResolver>.Instance);

        var charState1 = new CharacterVisualState(charId, "Alchemist Workshop", 1);
        var sceneState1 = new SceneVisualState(
            sessionId: sessionId,
            characterId: charId,
            location: "Alchemist Workshop",
            characterState: charState1,
            sceneRevision: 1,
            persistentChanges: new Dictionary<string, string>
            {
                ["CrystalFlask"] = "ShatteredOnWorkbench",
                ["Furnace"] = "BlazingBlueFlames"
            }
        );

        var prevSpec = new SceneSpecification(
            characterId: charId,
            location: "Alchemist Workshop",
            action: "knocked over the flask",
            sceneRevision: 1,
            sessionId: sessionId,
            turnId: turn1
        );

        var context = new SceneCompositionContext(
            CharacterId: charId,
            SessionId: sessionId,
            TurnId: turn2,
            SceneRevision: 2,
            PreviousScene: prevSpec
        );

        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Alchemist Workshop",
            actionHint: "examines the glowing residue",
            sessionId: sessionId,
            turnId: turn2
        );

        // Act
        var result = await resolver.ResolveAsync(new VisualContinuityRequest(intent, context, 2));

        // Assert: SameScene transition preserves world mutations
        Assert.Equal(SceneTransitionType.SameScene, result.TransitionType);
    }
}
