using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.CharacterReaction;

public sealed class CharacterReactionStateDeltaTests
{
    [Fact]
    public void ApplyDelta_ExtremePositiveAndNegativeDeltas_ClampsStrictlyWithin0And100()
    {
        var state = new CharacterStateSnapshot(
            energy: 10,
            mood: CharacterMood.Neutral,
            moodIntensity: 10,
            hunger: 95,
            socialNeed: 95,
            stress: 95,
            fitness: 10,
            intellect: 10,
            confidence: 10
        );

        // Apply huge deltas exceeding boundaries
        var modified = state.ApplyDelta(
            energyDelta: -50,       // 10 - 50 -> 0
            hungerDelta: +50,       // 95 + 50 -> 100
            socialNeedDelta: +50,   // 95 + 50 -> 100
            stressDelta: +50,       // 95 + 50 -> 100
            fitnessDelta: -50,      // 10 - 50 -> 0
            intellectDelta: -50,    // 10 - 50 -> 0
            confidenceDelta: -50,   // 10 - 50 -> 0
            moodIntensityDelta: 200 // -> 100
        );

        Assert.InRange(modified.Energy, 0, 100);
        Assert.InRange(modified.Hunger, 0, 100);
        Assert.InRange(modified.SocialNeed, 0, 100);
        Assert.InRange(modified.Stress, 0, 100);
        Assert.InRange(modified.Fitness, 0, 100);
        Assert.InRange(modified.Intellect, 0, 100);
        Assert.InRange(modified.Confidence, 0, 100);
        Assert.InRange(modified.MoodIntensity, 0, 100);

        Assert.Equal(0, modified.Energy);
        Assert.Equal(100, modified.Hunger);
        Assert.Equal(100, modified.SocialNeed);
        Assert.Equal(100, modified.Stress);
        Assert.Equal(0, modified.Fitness);
        Assert.Equal(0, modified.Intellect);
        Assert.Equal(0, modified.Confidence);
        Assert.Equal(100, modified.MoodIntensity);
    }
}
