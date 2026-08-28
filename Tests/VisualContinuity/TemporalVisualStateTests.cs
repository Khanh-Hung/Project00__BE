using Domain.Entities;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class TemporalVisualStateTests
{
    [Fact]
    public void CharacterVisualState_TracksTemporalValidity_AndInvalidatesCorrectly()
    {
        // Arrange
        var charId = Guid.NewGuid();
        var turn1 = Guid.NewGuid();
        var turn5 = Guid.NewGuid();

        var state = new CharacterVisualState(
            characterId: charId,
            location: "Sanctuary",
            sceneRevision: 1,
            outfit: "Ceremonial Silk Robe",
            validFromTurnId: turn1
        );

        Assert.Equal(turn1, state.ValidFromTurnId);
        Assert.Null(state.ValidUntilTurnId);
        Assert.True(state.IsActiveForTurn(turn1));

        // Act: Invalidate on turn 5
        state.Invalidate(turn5);

        // Assert: Expired state is no longer active
        Assert.Equal(turn5, state.ValidUntilTurnId);
        Assert.False(state.IsActiveForTurn(turn5));
    }

    [Fact]
    public void CharacterVisualMemory_Invalidation_PreventsZombieResurrection()
    {
        // Arrange
        var charId = Guid.NewGuid();
        var turn1 = Guid.NewGuid();
        var turn3 = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var memory = new CharacterVisualMemory(
            characterId: charId,
            visualProfileVersion: 1,
            sceneRevision: 1,
            artifactId: artifactId,
            context: "Red Silk Robe",
            sourceTurnId: turn1,
            validFromTurnId: turn1
        );

        Assert.True(memory.IsActiveForTurn(turn1));

        // Invalidate on turn 3
        memory.Invalidate(turn3);

        // Assert: Cannot resurrect in turn 4
        Assert.False(memory.IsActiveForTurn(Guid.NewGuid()));
        Assert.Equal(turn3, memory.ValidUntilTurnId);
    }
}
