using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.Perception;

public sealed class CharacterPerceptionDomainTests
{
    private readonly CharacterPerceptionPolicy _policy = new();

    #region 1. Boundary & Level Discretization Tests

    [Theory]
    [InlineData(0, HungerLevel.Satisfied)]
    [InlineData(20, HungerLevel.Satisfied)]
    [InlineData(21, HungerLevel.SlightlyHungry)]
    [InlineData(40, HungerLevel.SlightlyHungry)]
    [InlineData(41, HungerLevel.Hungry)]
    [InlineData(60, HungerLevel.Hungry)]
    [InlineData(61, HungerLevel.VeryHungry)]
    [InlineData(80, HungerLevel.VeryHungry)]
    [InlineData(81, HungerLevel.Starving)]
    [InlineData(100, HungerLevel.Starving)]
    public void HungerLevel_DiscretizesAcrossAllBoundaries(int hunger, HungerLevel expected)
    {
        var state = new CharacterStateSnapshot(hunger: hunger);
        var experience = _policy.Evaluate(state);

        Assert.Equal(expected, experience.Hunger.Level);
        Assert.Equal(hunger, experience.Hunger.RawValue);
    }

    [Theory]
    [InlineData(0, EnergyLevel.Exhausted)]
    [InlineData(20, EnergyLevel.Exhausted)]
    [InlineData(21, EnergyLevel.Tired)]
    [InlineData(40, EnergyLevel.Tired)]
    [InlineData(41, EnergyLevel.Moderate)]
    [InlineData(60, EnergyLevel.Moderate)]
    [InlineData(61, EnergyLevel.Energized)]
    [InlineData(80, EnergyLevel.Energized)]
    [InlineData(81, EnergyLevel.HighlyEnergized)]
    [InlineData(100, EnergyLevel.HighlyEnergized)]
    public void EnergyLevel_DiscretizesAcrossAllBoundaries(int energy, EnergyLevel expected)
    {
        var state = new CharacterStateSnapshot(energy: energy);
        var experience = _policy.Evaluate(state);

        Assert.Equal(expected, experience.Energy.Level);
        Assert.Equal(energy, experience.Energy.RawValue);
    }

    [Theory]
    [InlineData(0, StressLevel.Calm)]
    [InlineData(20, StressLevel.Calm)]
    [InlineData(21, StressLevel.MildPressure)]
    [InlineData(40, StressLevel.MildPressure)]
    [InlineData(41, StressLevel.Stressed)]
    [InlineData(60, StressLevel.Stressed)]
    [InlineData(61, StressLevel.HighlyStressed)]
    [InlineData(80, StressLevel.HighlyStressed)]
    [InlineData(81, StressLevel.Overwhelmed)]
    [InlineData(100, StressLevel.Overwhelmed)]
    public void StressLevel_DiscretizesAcrossAllBoundaries(int stress, StressLevel expected)
    {
        var state = new CharacterStateSnapshot(stress: stress);
        var experience = _policy.Evaluate(state);

        Assert.Equal(expected, experience.Stress.Level);
        Assert.Equal(stress, experience.Stress.RawValue);
    }

    [Theory]
    [InlineData(0, SocialNeedLevel.SociallySatisfied)]
    [InlineData(20, SocialNeedLevel.SociallySatisfied)]
    [InlineData(21, SocialNeedLevel.MildSocialNeed)]
    [InlineData(40, SocialNeedLevel.MildSocialNeed)]
    [InlineData(41, SocialNeedLevel.WantsCompany)]
    [InlineData(60, SocialNeedLevel.WantsCompany)]
    [InlineData(61, SocialNeedLevel.StrongNeedForCompany)]
    [InlineData(80, SocialNeedLevel.StrongNeedForCompany)]
    [InlineData(81, SocialNeedLevel.CravesConnection)]
    [InlineData(100, SocialNeedLevel.CravesConnection)]
    public void SocialNeedLevel_DiscretizesAcrossAllBoundaries(int socialNeed, SocialNeedLevel expected)
    {
        var state = new CharacterStateSnapshot(socialNeed: socialNeed);
        var experience = _policy.Evaluate(state);

        Assert.Equal(expected, experience.SocialNeed.Level);
        Assert.Equal(socialNeed, experience.SocialNeed.RawValue);
    }

    [Theory]
    [InlineData(0, ComfortLevel.VeryUncomfortable)]
    [InlineData(20, ComfortLevel.VeryUncomfortable)]
    [InlineData(21, ComfortLevel.Uncomfortable)]
    [InlineData(40, ComfortLevel.Uncomfortable)]
    [InlineData(41, ComfortLevel.Neutral)]
    [InlineData(60, ComfortLevel.Neutral)]
    [InlineData(61, ComfortLevel.Comfortable)]
    [InlineData(80, ComfortLevel.Comfortable)]
    [InlineData(81, ComfortLevel.VeryComfortable)]
    [InlineData(100, ComfortLevel.VeryComfortable)]
    public void ComfortLevel_DiscretizesAcrossAllBoundaries(int comfort, ComfortLevel expected)
    {
        var state = new CharacterStateSnapshot(comfort: comfort);
        var experience = _policy.Evaluate(state);

        Assert.Equal(expected, experience.Comfort.Level);
        Assert.Equal(comfort, experience.Comfort.RawValue);
    }

    [Theory]
    [InlineData(0, MoodPerceptionLevel.Depressed)]
    [InlineData(20, MoodPerceptionLevel.Depressed)]
    [InlineData(21, MoodPerceptionLevel.Low)]
    [InlineData(40, MoodPerceptionLevel.Low)]
    [InlineData(41, MoodPerceptionLevel.Neutral)]
    [InlineData(60, MoodPerceptionLevel.Neutral)]
    [InlineData(61, MoodPerceptionLevel.Good)]
    [InlineData(80, MoodPerceptionLevel.Good)]
    [InlineData(81, MoodPerceptionLevel.Elated)]
    [InlineData(100, MoodPerceptionLevel.Elated)]
    public void MoodPerceptionLevel_DiscretizesAcrossAllBoundaries(int moodScalar, MoodPerceptionLevel expected)
    {
        var state = new CharacterStateSnapshot(moodScalar: moodScalar);
        var experience = _policy.Evaluate(state);

        Assert.Equal(expected, experience.Mood.Level);
        Assert.Equal(moodScalar, experience.Mood.RawValue);
    }

    #endregion

    #region 2. Determinism Tests

    [Fact]
    public void Evaluate_Is100PercentDeterministic_Over100Evaluations()
    {
        var state = new CharacterStateSnapshot(
            energy: 65,
            hunger: 45,
            socialNeed: 55,
            stress: 35,
            comfort: 70,
            moodScalar: 60m,
            version: 7
        );
        var psych = new PsychologyProfile(
            HungerSensitivity: 1.2m,
            FatigueSensitivity: 0.9m,
            StressSensitivity: 1.1m
        );
        var context = new CharacterPerceptionContext(
            EvaluatedAtUtc: new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc),
            CharacterId: Guid.NewGuid(),
            CurrentActivity: "Reading"
        );

        var baseline = _policy.Evaluate(state, psych, context);

        for (int i = 0; i < 100; i++)
        {
            var next = _policy.Evaluate(state, psych, context);
            Assert.Equal(baseline, next);
        }
    }

    #endregion

    #region 3. Personality Sensitivity Tests

    [Fact]
    public void PsychologySensitivity_ModifiesPerceivedIntensity_WithoutMutatingState()
    {
        var state = new CharacterStateSnapshot(hunger: 70, version: 3);

        var normalPsych = new PsychologyProfile(HungerSensitivity: 1.0m);
        var highSensPsych = new PsychologyProfile(HungerSensitivity: 1.4m);
        var lowSensPsych = new PsychologyProfile(HungerSensitivity: 0.5m);

        var normalExp = _policy.Evaluate(state, normalPsych);
        var highSensExp = _policy.Evaluate(state, highSensPsych);
        var lowSensExp = _policy.Evaluate(state, lowSensPsych);

        // State remains unchanged
        Assert.Equal(70, state.Hunger);
        Assert.Equal(3, state.Version);

        // Raw values in perception are equal to authoritative state
        Assert.Equal(70, normalExp.Hunger.RawValue);
        Assert.Equal(70, highSensExp.Hunger.RawValue);
        Assert.Equal(70, lowSensExp.Hunger.RawValue);

        // Perceived intensity differs deterministically based on psychology
        Assert.Equal(0.7000, normalExp.Hunger.Intensity.Value, 4);
        Assert.Equal(0.9800, highSensExp.Hunger.Intensity.Value, 4); // 0.70 * 1.4 = 0.98
        Assert.Equal(0.3500, lowSensExp.Hunger.Intensity.Value, 4); // 0.70 * 0.5 = 0.35
    }

    #endregion

    #region 4. Dominant Need Precedence & Calculation Tests

    [Fact]
    public void DominantNeed_IdentifiesClearWinner()
    {
        // High hunger (90), others low/normal
        var state = new CharacterStateSnapshot(
            hunger: 90,
            energy: 80,
            stress: 10,
            socialNeed: 20,
            comfort: 80
        );

        var exp = _policy.Evaluate(state);
        Assert.Equal(DominantNeed.Hunger, exp.DominantNeed);
    }

    [Fact]
    public void DominantNeed_ResolvesTies_AccordingToStrictPrecedence()
    {
        // Hunger Pressure = 0.80, Fatigue Pressure = (100 - 20) / 100 = 0.80
        // Strict Precedence: Hunger > Energy
        var stateTiedHungerEnergy = new CharacterStateSnapshot(
            hunger: 80,
            energy: 20,
            stress: 10,
            socialNeed: 10,
            comfort: 80
        );

        var expHungerEnergy = _policy.Evaluate(stateTiedHungerEnergy);
        Assert.Equal(DominantNeed.Hunger, expHungerEnergy.DominantNeed);

        // Fatigue Pressure = (100 - 20) / 100 = 0.80, SocialNeed Pressure = 0.80
        // Strict Precedence: Energy > SocialNeed
        var stateTiedEnergySocial = new CharacterStateSnapshot(
            hunger: 20,
            energy: 20,
            stress: 10,
            socialNeed: 80,
            comfort: 80
        );

        var expEnergySocial = _policy.Evaluate(stateTiedEnergySocial);
        Assert.Equal(DominantNeed.Energy, expEnergySocial.DominantNeed);

        // SocialNeed Pressure = 0.75, Discomfort Pressure = (100 - 25) / 100 = 0.75
        // Strict Precedence: SocialNeed > Comfort
        var stateTiedSocialComfort = new CharacterStateSnapshot(
            hunger: 20,
            energy: 80,
            stress: 10,
            socialNeed: 75,
            comfort: 25
        );

        var expSocialComfort = _policy.Evaluate(stateTiedSocialComfort);
        Assert.Equal(DominantNeed.SocialNeed, expSocialComfort.DominantNeed);
    }

    [Fact]
    public void DominantNeed_ReturnsNone_WhenAllNeedsAreSatisfied()
    {
        // All metrics within optimal ranges (hunger low, energy high, stress low, comfort high)
        var state = new CharacterStateSnapshot(
            hunger: 10,
            energy: 90,
            stress: 10,
            socialNeed: 10,
            comfort: 90
        );

        var exp = _policy.Evaluate(state);
        Assert.Equal(DominantNeed.None, exp.DominantNeed);
    }

    #endregion

    #region 5. StateVersion & Context Preservation Tests

    [Fact]
    public void StateVersionAndContext_ArePreservedInDerivedExperience()
    {
        var charId = Guid.NewGuid();
        var evalTime = new DateTime(2026, 9, 3, 15, 30, 0, DateTimeKind.Utc);
        var state = new CharacterStateSnapshot(version: 42);
        var context = new CharacterPerceptionContext(
            EvaluatedAtUtc: evalTime,
            CharacterId: charId,
            CurrentActivity: "Meditating",
            Location: "Sanctuary Garden"
        );

        var exp = _policy.Evaluate(state, context: context);

        Assert.Equal(42, exp.StateVersion);
        Assert.Equal(charId, exp.CharacterId);
        Assert.Equal(evalTime, exp.EvaluatedAtUtc);
    }

    #endregion

    #region 6. Input Validation & Fault Injection Tests

    [Fact]
    public void Evaluate_ThrowsArgumentNullException_WhenStateIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => _policy.Evaluate(null!));
    }

    [Fact]
    public void PerceptionIntensity_ThrowsOnInvalidDouble()
    {
        Assert.Throws<ArgumentException>(() => new PerceptionIntensity(double.NaN));
        Assert.Throws<ArgumentException>(() => new PerceptionIntensity(double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => new PerceptionIntensity(double.NegativeInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerceptionIntensity(-0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerceptionIntensity(1.05));
    }

    [Fact]
    public void Evaluate_ThrowsOnNegativePsychologySensitivity()
    {
        var state = new CharacterStateSnapshot();
        var invalidPsych = new PsychologyProfile(HungerSensitivity: -0.5m);

        Assert.Throws<ArgumentOutOfRangeException>(() => _policy.Evaluate(state, invalidPsych));
    }

    #endregion

    #region 7. Thread-Safety & Concurrent Read-Only Execution

    [Fact]
    public async Task Evaluate_IsSafeForConcurrentExecution_AcrossMultipleWorkers()
    {
        var state = new CharacterStateSnapshot(
            hunger: 65,
            energy: 40,
            socialNeed: 70,
            stress: 50,
            comfort: 60,
            moodScalar: 55m,
            version: 12
        );
        var psych = new PsychologyProfile(
            HungerSensitivity: 1.1m,
            FatigueSensitivity: 1.2m
        );
        var context = new CharacterPerceptionContext(
            EvaluatedAtUtc: new DateTime(2026, 9, 3, 14, 0, 0, DateTimeKind.Utc),
            CharacterId: Guid.NewGuid()
        );

        var baseline = _policy.Evaluate(state, psych, context);

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            return _policy.Evaluate(state, psych, context);
        }));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(baseline, r));
    }

    #endregion
}
