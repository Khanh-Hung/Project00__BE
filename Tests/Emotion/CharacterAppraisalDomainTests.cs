using System;
using System.Linq;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.Emotion;

public sealed class CharacterAppraisalDomainTests
{
    private readonly CharacterInternalExperiencePolicy _experiencePolicy = new();
    private readonly CharacterAppraisalPolicy _appraisalPolicy = new();

    private static CharacterPerceptionContext CreateContext(Guid? id = null) =>
        new(new DateTime(2026, 9, 3, 16, 0, 0, DateTimeKind.Utc), id ?? Guid.NewGuid());

    #region 1. Boundary & Semantic Mapping Tests

    [Fact]
    public void Hunger_AppraisesTo_PhysicalDeprivation_WhenHungry()
    {
        var state = new CharacterStateSnapshot(hunger: 80);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var appraisal = _appraisalPolicy.Evaluate(exp);

        Assert.Equal(AppraisalType.PhysicalDeprivation, appraisal.Type);
        Assert.Equal(AppraisalPolarity.Negative, appraisal.Polarity);
        Assert.Equal(AppraisalSource.Hunger, appraisal.Source);
        Assert.True(appraisal.Intensity > 0.0);
    }

    [Fact]
    public void Hunger_AppraisesTo_NeutralRestoration_WhenSatisfied()
    {
        var state = new CharacterStateSnapshot(hunger: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var all = _appraisalPolicy.EvaluateAll(exp);
        var hungerAppraisal = all.First(a => a.Source == AppraisalSource.Hunger);

        Assert.Equal(AppraisalType.PhysicalRestoration, hungerAppraisal.Type);
        Assert.Equal(AppraisalPolarity.Neutral, hungerAppraisal.Polarity);
        Assert.Equal(0.0, hungerAppraisal.Intensity);
    }

    [Fact]
    public void Energy_LowEnergy_AppraisesTo_FatigueNegative()
    {
        // Low Energy (10) -> High Fatigue intensity (0.90)
        var state = new CharacterStateSnapshot(energy: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var appraisal = _appraisalPolicy.Evaluate(exp);

        Assert.Equal(AppraisalType.Fatigue, appraisal.Type);
        Assert.Equal(AppraisalPolarity.Negative, appraisal.Polarity);
        Assert.Equal(AppraisalSource.Energy, appraisal.Source);
        Assert.Equal(0.90, appraisal.Intensity, 2);
    }

    [Fact]
    public void Energy_HighEnergy_AppraisesTo_RecoveryPositive()
    {
        // High Energy (90) -> Recovery Positive (0.90)
        var state = new CharacterStateSnapshot(energy: 90);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var all = _appraisalPolicy.EvaluateAll(exp);
        var energyAppraisal = all.First(a => a.Source == AppraisalSource.Energy);

        Assert.Equal(AppraisalType.Recovery, energyAppraisal.Type);
        Assert.Equal(AppraisalPolarity.Positive, energyAppraisal.Polarity);
        Assert.Equal(0.90, energyAppraisal.Intensity, 2);
    }

    [Fact]
    public void Comfort_LowComfort_AppraisesTo_DiscomfortNegative()
    {
        // Low Comfort (15) -> High Discomfort intensity (0.85)
        var state = new CharacterStateSnapshot(comfort: 15);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var all = _appraisalPolicy.EvaluateAll(exp);
        var comfortAppraisal = all.First(a => a.Source == AppraisalSource.Comfort);

        Assert.Equal(AppraisalType.Discomfort, comfortAppraisal.Type);
        Assert.Equal(AppraisalPolarity.Negative, comfortAppraisal.Polarity);
        Assert.Equal(0.85, comfortAppraisal.Intensity, 2);
    }

    [Fact]
    public void Comfort_HighComfort_AppraisesTo_ComfortPositive()
    {
        var state = new CharacterStateSnapshot(comfort: 85);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var all = _appraisalPolicy.EvaluateAll(exp);
        var comfortAppraisal = all.First(a => a.Source == AppraisalSource.Comfort);

        Assert.Equal(AppraisalType.Comfort, comfortAppraisal.Type);
        Assert.Equal(AppraisalPolarity.Positive, comfortAppraisal.Polarity);
        Assert.Equal(0.85, comfortAppraisal.Intensity, 2);
    }

    [Fact]
    public void Stress_HighStress_AppraisesTo_StressPressureNegative()
    {
        var state = new CharacterStateSnapshot(stress: 85, energy: 50);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var appraisal = _appraisalPolicy.Evaluate(exp);

        Assert.Equal(AppraisalType.StressPressure, appraisal.Type);
        Assert.Equal(AppraisalPolarity.Negative, appraisal.Polarity);
        Assert.Equal(AppraisalSource.Stress, appraisal.Source);
        Assert.Equal(0.85, appraisal.Intensity, 2);
    }

    [Fact]
    public void Stress_LowStress_AppraisesTo_NeutralSafety()
    {
        var state = new CharacterStateSnapshot(stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var all = _appraisalPolicy.EvaluateAll(exp);
        var stressAppraisal = all.First(a => a.Source == AppraisalSource.Stress);

        Assert.Equal(AppraisalType.Safety, stressAppraisal.Type);
        Assert.Equal(AppraisalPolarity.Neutral, stressAppraisal.Polarity);
        Assert.Equal(0.0, stressAppraisal.Intensity);
    }

    [Fact]
    public void SocialNeed_HighSocialNeed_AppraisesTo_SocialDeprivationNegative()
    {
        var state = new CharacterStateSnapshot(socialNeed: 80);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var appraisal = _appraisalPolicy.Evaluate(exp);

        Assert.Equal(AppraisalType.SocialDeprivation, appraisal.Type);
        Assert.Equal(AppraisalPolarity.Negative, appraisal.Polarity);
        Assert.Equal(AppraisalSource.SocialNeed, appraisal.Source);
        Assert.Equal(0.80, appraisal.Intensity, 2);
    }

    [Fact]
    public void SocialNeed_LowSocialNeed_AppraisesTo_NeutralConnection()
    {
        var state = new CharacterStateSnapshot(socialNeed: 15);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var all = _appraisalPolicy.EvaluateAll(exp);
        var socialAppraisal = all.First(a => a.Source == AppraisalSource.SocialNeed);

        Assert.Equal(AppraisalType.SocialConnection, socialAppraisal.Type);
        Assert.Equal(AppraisalPolarity.Neutral, socialAppraisal.Polarity);
        Assert.Equal(0.0, socialAppraisal.Intensity);
    }

    [Fact]
    public void Mood_LowMood_AppraisesTo_NegativeMood()
    {
        var state = new CharacterStateSnapshot(moodScalar: 15m);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var all = _appraisalPolicy.EvaluateAll(exp);
        var moodAppraisal = all.First(a => a.Source == AppraisalSource.Mood);

        Assert.Equal(AppraisalType.NegativeMood, moodAppraisal.Type);
        Assert.Equal(AppraisalPolarity.Negative, moodAppraisal.Polarity);
        Assert.Equal(0.85, moodAppraisal.Intensity, 2);
    }

    [Fact]
    public void Mood_HighMood_AppraisesTo_PositiveMood()
    {
        var state = new CharacterStateSnapshot(moodScalar: 85m);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var all = _appraisalPolicy.EvaluateAll(exp);
        var moodAppraisal = all.First(a => a.Source == AppraisalSource.Mood);

        Assert.Equal(AppraisalType.PositiveMood, moodAppraisal.Type);
        Assert.Equal(AppraisalPolarity.Positive, moodAppraisal.Polarity);
        Assert.Equal(0.85, moodAppraisal.Intensity, 2);
    }

    #endregion

    #region 2. Dominant Appraisal: Highest Intensity First & Tie-Breaking (P0 Regression Tests)

    [Fact]
    public void DominantAppraisal_SelectsPositive_WhenPositiveIntensityIsHigherThanNegative()
    {
        // Recovery (Positive, 0.90) vs StressPressure (Negative, 0.25)
        // Highest Intensity (0.90 > 0.25) MUST WIN, regardless of polarity
        var state = new CharacterStateSnapshot(energy: 90, stress: 25, hunger: 10, socialNeed: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var dominant = _appraisalPolicy.Evaluate(exp);

        Assert.Equal(AppraisalSource.Energy, dominant.Source);
        Assert.Equal(AppraisalType.Recovery, dominant.Type);
        Assert.Equal(AppraisalPolarity.Positive, dominant.Polarity);
        Assert.Equal(0.90, dominant.Intensity, 2);
    }

    [Fact]
    public void DominantAppraisal_SelectsNegative_WhenNegativeIntensityIsHigherThanPositive()
    {
        // StressPressure (Negative, 0.85) vs Comfort (Positive, 0.50)
        // Highest Intensity (0.85 > 0.50) MUST WIN
        var state = new CharacterStateSnapshot(stress: 85, comfort: 50, energy: 50);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var dominant = _appraisalPolicy.Evaluate(exp);

        Assert.Equal(AppraisalSource.Stress, dominant.Source);
        Assert.Equal(AppraisalType.StressPressure, dominant.Type);
        Assert.Equal(AppraisalPolarity.Negative, dominant.Polarity);
        Assert.Equal(0.85, dominant.Intensity, 2);
    }

    [Fact]
    public void DominantAppraisal_ResolvesTies_ByPrecedence_StressOverHunger()
    {
        // Tied intensity: Stress Pressure = 0.80, Hunger = 0.80
        var state = new CharacterStateSnapshot(stress: 80, hunger: 80, energy: 50);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var dominant = _appraisalPolicy.Evaluate(exp);

        // Precedence: Stress > Hunger
        Assert.Equal(AppraisalSource.Stress, dominant.Source);
        Assert.Equal(AppraisalType.StressPressure, dominant.Type);
        Assert.Equal(0.80, dominant.Intensity, 2);
    }

    [Fact]
    public void DominantAppraisal_ResolvesTies_ByPrecedence_StressOverRecovery()
    {
        // Tied intensity: Stress Pressure (Negative 0.80) vs Recovery (Positive 0.80)
        // Tied intensity -> Precedence: Stress (1) > Energy (4)
        var state = new CharacterStateSnapshot(stress: 80, energy: 80);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var dominant = _appraisalPolicy.Evaluate(exp);

        Assert.Equal(AppraisalSource.Stress, dominant.Source);
        Assert.Equal(AppraisalType.StressPressure, dominant.Type);
        Assert.Equal(0.80, dominant.Intensity, 2);
    }

    [Fact]
    public void DominantAppraisal_ResolvesTies_ByPrecedence_HungerOverRecovery()
    {
        // Tied intensity: Hunger (Negative 0.80) vs Recovery (Positive 0.80)
        // Tied intensity -> Precedence: Hunger (2) > Energy (4)
        var state = new CharacterStateSnapshot(hunger: 80, energy: 80, stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var dominant = _appraisalPolicy.Evaluate(exp);

        Assert.Equal(AppraisalSource.Hunger, dominant.Source);
        Assert.Equal(AppraisalType.PhysicalDeprivation, dominant.Type);
        Assert.Equal(0.80, dominant.Intensity, 2);
    }

    #endregion

    #region 3. Invariant Validation & Determinism Tests

    [Fact]
    public void CharacterAppraisal_RejectsInvalidIntensity()
    {
        Assert.Throws<ArgumentException>(() => new CharacterAppraisal(AppraisalType.Fatigue, AppraisalPolarity.Negative, double.NaN, AppraisalSource.Energy));
        Assert.Throws<ArgumentException>(() => new CharacterAppraisal(AppraisalType.Fatigue, AppraisalPolarity.Negative, double.PositiveInfinity, AppraisalSource.Energy));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterAppraisal(AppraisalType.Fatigue, AppraisalPolarity.Negative, -0.01, AppraisalSource.Energy));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterAppraisal(AppraisalType.Fatigue, AppraisalPolarity.Negative, 1.01, AppraisalSource.Energy));
    }

    [Fact]
    public void Evaluate_ThrowsArgumentNullException_WhenExperienceIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => _appraisalPolicy.Evaluate(null!));
        Assert.Throws<ArgumentNullException>(() => _appraisalPolicy.EvaluateAll(null!));
    }

    [Fact]
    public void Evaluate_Is100PercentDeterministic_Over100Evaluations()
    {
        var state = new CharacterStateSnapshot(hunger: 65, energy: 45, stress: 55, version: 3);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var baseline = _appraisalPolicy.Evaluate(exp);

        for (int i = 0; i < 100; i++)
        {
            var next = _appraisalPolicy.Evaluate(exp);
            Assert.Equal(baseline, next);
        }
    }

    [Fact]
    public void StateAndExperience_AreNotMutated_DuringAppraisal()
    {
        var state = new CharacterStateSnapshot(hunger: 70, energy: 30, version: 10);
        var context = CreateContext();
        var exp = _experiencePolicy.Evaluate(state, context);

        _appraisalPolicy.Evaluate(exp);

        // State remains completely intact
        Assert.Equal(70, state.Hunger);
        Assert.Equal(30, state.Energy);
        Assert.Equal(10, state.Version);

        // Experience remains intact
        Assert.Equal(70, exp.Hunger.RawValue);
        Assert.Equal(30, exp.Energy.RawValue);
        Assert.Equal(10, exp.StateVersion);
    }

    #endregion
}
