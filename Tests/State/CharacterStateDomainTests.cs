using Domain.Entities;
using Domain.ValueObjects;
using Xunit;

namespace Tests.State;

public sealed class CharacterStateDomainTests
{
    [Fact]
    public void Constructor_InitializesValuesCorrectly_AndSetsVersionToOne()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var state = new CharacterState(
            characterId: charId,
            initializedAtUtc: now,
            hunger: 25m,
            energy: 75m,
            mood: 60m,
            stress: 15m,
            socialNeed: 40m,
            comfort: 85m
        );

        Assert.Equal(charId, state.CharacterId);
        Assert.Equal(25m, state.Hunger);
        Assert.Equal(75m, state.Energy);
        Assert.Equal(60m, state.Mood);
        Assert.Equal(15m, state.Stress);
        Assert.Equal(40m, state.SocialNeed);
        Assert.Equal(85m, state.Comfort);
        Assert.Equal(now, state.LastEvolvedAtUtc);
        Assert.Equal(1, state.Version);
    }

    [Fact]
    public void Constructor_RejectsOutOfRangeValues_ThrowsArgumentOutOfRangeException()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterState(charId, now, hunger: -1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterState(charId, now, hunger: 105m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterState(charId, now, energy: -5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterState(charId, now, mood: 120m));
        Assert.Throws<ArgumentException>(() => new CharacterState(Guid.Empty, now));
    }

    [Fact]
    public void ApplyDelta_ClampsValuesStrictlyBetween0And100_AndIncrementsVersion()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var state = CharacterState.CreateDefault(charId, now);
        int initialVersion = state.Version;

        // Apply massive positive delta
        var delta = new CharacterStateDelta(
            hungerDelta: 150m,
            energyDelta: 150m,
            moodDelta: 150m,
            stressDelta: 150m,
            socialNeedDelta: 150m,
            comfortDelta: 150m
        );

        state.ApplyDelta(delta);

        Assert.Equal(100m, state.Hunger);
        Assert.Equal(100m, state.Energy);
        Assert.Equal(100m, state.Mood);
        Assert.Equal(100m, state.Stress);
        Assert.Equal(100m, state.SocialNeed);
        Assert.Equal(100m, state.Comfort);
        Assert.Equal(initialVersion + 1, state.Version);

        // Apply massive negative delta
        var negativeDelta = new CharacterStateDelta(
            hungerDelta: -200m,
            energyDelta: -200m,
            moodDelta: -200m,
            stressDelta: -200m,
            socialNeedDelta: -200m,
            comfortDelta: -200m
        );

        state.ApplyDelta(negativeDelta);

        Assert.Equal(0m, state.Hunger);
        Assert.Equal(0m, state.Energy);
        Assert.Equal(0m, state.Mood);
        Assert.Equal(0m, state.Stress);
        Assert.Equal(0m, state.SocialNeed);
        Assert.Equal(0m, state.Comfort);
        Assert.Equal(initialVersion + 2, state.Version);
    }

    [Fact]
    public void Evolve_AdvancesLastEvolvedAtUtc_AndIncrementsVersion()
    {
        var charId = Guid.NewGuid();
        var t0 = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddHours(2);
        var state = CharacterState.CreateDefault(charId, t0);

        var delta = new CharacterStateDelta(hungerDelta: 8m, energyDelta: -10m);
        state.Evolve(delta, t1);

        Assert.Equal(t1, state.LastEvolvedAtUtc);
        Assert.Equal(2, state.Version);
        Assert.Equal(28m, state.Hunger); // 20 + 8
        Assert.Equal(70m, state.Energy); // 80 - 10
    }

    [Fact]
    public void Evolve_BackwardsInTime_ThrowsInvalidOperationException()
    {
        var charId = Guid.NewGuid();
        var t0 = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);
        var state = CharacterState.CreateDefault(charId, t0);

        var past = t0.AddMinutes(-5);
        Assert.Throws<InvalidOperationException>(() => state.Evolve(CharacterStateDelta.Zero, past));
    }

    [Fact]
    public void CharacterStateDelta_Create_RejectsNaNAndInfinity()
    {
        Assert.Throws<ArgumentException>(() => CharacterStateDelta.Create(hungerDelta: double.NaN));
        Assert.Throws<ArgumentException>(() => CharacterStateDelta.Create(energyDelta: double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => CharacterStateDelta.Create(stressDelta: double.NegativeInfinity));
    }

    [Fact]
    public void ToSnapshot_ProducesAccurateImmutableRepresentation()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var state = new CharacterState(
            charId, now,
            hunger: 45.6m,
            energy: 82.3m,
            mood: 78.9m,
            stress: 22.1m,
            socialNeed: 63.4m,
            comfort: 74.5m
        );

        var snapshot = state.ToSnapshot();

        Assert.Equal(46, snapshot.Hunger);
        Assert.Equal(82, snapshot.Energy);
        Assert.Equal(78.9m, snapshot.MoodScalar);
        Assert.Equal(22, snapshot.Stress);
        Assert.Equal(63, snapshot.SocialNeed);
        Assert.Equal(75, snapshot.Comfort);
        Assert.Equal(now, snapshot.LastEvolvedAtUtc);
        Assert.Equal(1, snapshot.Version);
    }
}
