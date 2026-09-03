using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.State;

public sealed class CharacterStateEvolutionPolicyTests
{
    private readonly CharacterStateEvolutionPolicy _policy = new();

    [Fact]
    public void CalculateEvolutionDelta_WithIdenticalTimestamps_ReturnsZeroDelta()
    {
        var now = DateTime.UtcNow;
        var state = CharacterStateSnapshot.CreateDefault();

        var delta = _policy.CalculateEvolutionDelta(state, now, now);

        Assert.True(delta.IsZero);
    }

    [Fact]
    public void CalculateEvolutionDelta_WithTargetEarlierThanLastEvolved_ThrowsInvalidOperationException()
    {
        var now = DateTime.UtcNow;
        var past = now.AddHours(-1);
        var state = CharacterStateSnapshot.CreateDefault();

        Assert.Throws<InvalidOperationException>(() =>
            _policy.CalculateEvolutionDelta(state, now, past));
    }

    [Fact]
    public void CalculateEvolutionDelta_ExactlyOneHour_AppliesHourlyRatesDeterministically()
    {
        var t0 = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddHours(1);
        var state = new CharacterStateSnapshot(
            energy: 80,
            hunger: 20,
            socialNeed: 30,
            stress: 10,
            comfort: 80
        );

        var delta = _policy.CalculateEvolutionDelta(state, t0, t1);

        Assert.Equal(4.0m, delta.HungerDelta);       // +4/hr
        Assert.Equal(-5.0m, delta.EnergyDelta);     // -5/hr
        Assert.Equal(1.0m, delta.StressDelta);      // +1/hr
        Assert.Equal(2.0m, delta.SocialNeedDelta);  // +2/hr
        Assert.Equal(-1.0m, delta.ComfortDelta);    // -1/hr
    }

    [Fact]
    public void CalculateEvolutionDelta_LargeTimeGap_CalculatedDirectlyWithoutLoops()
    {
        var t0 = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddHours(24); // 1 full day
        var state = CharacterStateSnapshot.CreateDefault();

        var delta = _policy.CalculateEvolutionDelta(state, t0, t1);

        Assert.Equal(96.0m, delta.HungerDelta);      // 24 * 4
        Assert.Equal(-120.0m, delta.EnergyDelta);   // 24 * -5
        Assert.Equal(24.0m, delta.StressDelta);     // 24 * 1
        Assert.Equal(48.0m, delta.SocialNeedDelta); // 24 * 2
        Assert.Equal(-24.0m, delta.ComfortDelta);   // 24 * -1
    }

    [Fact]
    public void CalculateEvolutionDelta_IsDeterministic_AcrossMultipleCalls()
    {
        var t0 = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMinutes(45);
        var state = CharacterStateSnapshot.CreateDefault();

        var delta1 = _policy.CalculateEvolutionDelta(state, t0, t1);
        var delta2 = _policy.CalculateEvolutionDelta(state, t0, t1);

        Assert.Equal(delta1.HungerDelta, delta2.HungerDelta);
        Assert.Equal(delta1.EnergyDelta, delta2.EnergyDelta);
        Assert.Equal(delta1.MoodDelta, delta2.MoodDelta);
        Assert.Equal(delta1.StressDelta, delta2.StressDelta);
        Assert.Equal(delta1.SocialNeedDelta, delta2.SocialNeedDelta);
        Assert.Equal(delta1.ComfortDelta, delta2.ComfortDelta);
    }
}
