using Domain.Entities;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class TemporalVisualStateTests
{
    [Fact]
    public void CharacterVisualState_TracksTemporalValidity_AcrossRevisionRanges()
    {
        // Arrange: ValidFromRevision = 5, ValidUntilRevision = 10
        var charId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var state = new CharacterVisualState(
            characterId: charId,
            location: "Sanctuary",
            sceneRevision: 5,
            outfit: "Ceremonial Silk Robe",
            validFromTurnId: turnId,
            validFromRevision: 5,
            validUntilRevision: 10
        );

        // Before start: Revision 4 -> Inactive
        Assert.False(state.IsActiveForRevision(4));

        // Active range: Revision 5 to 9 -> Active
        Assert.True(state.IsActiveForRevision(5));
        Assert.True(state.IsActiveForRevision(7));
        Assert.True(state.IsActiveForRevision(9));

        // After invalidation: Revision 10 and beyond -> Inactive
        Assert.False(state.IsActiveForRevision(10));
        Assert.False(state.IsActiveForRevision(12));
    }

    [Fact]
    public void CharacterVisualMemory_TemporalInvalidation_ExcludesHistoricalRevisions()
    {
        var charId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var turn1 = Guid.NewGuid();
        var turn3 = Guid.NewGuid();

        var memory = new CharacterVisualMemory(
            characterId: charId,
            visualProfileVersion: 1,
            sceneRevision: 2,
            artifactId: artifactId,
            context: "Red Silk Robe",
            outfit: "Red Silk Robe",
            sourceTurnId: turn1,
            validFromTurnId: turn1,
            validFromRevision: 2
        );

        // Active at revision 2 and 3
        Assert.True(memory.IsActiveForRevision(2));
        Assert.True(memory.IsActiveForRevision(3));

        // Invalidate starting at revision 4
        memory.Invalidate(turn3, supersededByRevision: 4);

        // Before start: Revision 1 -> Inactive
        Assert.False(memory.IsActiveForRevision(1));

        // Active range: Revision 2 and 3 -> Active
        Assert.True(memory.IsActiveForRevision(2));
        Assert.True(memory.IsActiveForRevision(3));

        // After invalidation: Revision 4 and beyond -> Inactive
        Assert.False(memory.IsActiveForRevision(4));
        Assert.False(memory.IsActiveForRevision(5));
    }
}
