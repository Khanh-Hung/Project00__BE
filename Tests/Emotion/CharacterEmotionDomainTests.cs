using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.Emotion;

public sealed class CharacterEmotionDomainTests
{
    private readonly CharacterInternalExperiencePolicy _experiencePolicy = new();
    private readonly CharacterAppraisalPolicy _appraisalPolicy = new();
    private readonly CharacterEmotionPolicy _emotionPolicy = new();

    private static CharacterPerceptionContext CreateContext(Guid? id = null) =>
        new(new DateTime(2026, 9, 3, 16, 0, 0, DateTimeKind.Utc), id ?? Guid.NewGuid());

    #region 1. Emotion Mapping from Appraisals Tests

    [Fact]
    public void PhysicalDeprivation_MapsTo_Frustration_WhenHighIntensity()
    {
        var appraisal = new CharacterAppraisal(AppraisalType.PhysicalDeprivation, AppraisalPolarity.Negative, 0.85, AppraisalSource.Hunger);

        // Direct appraisal evaluation without needing CharacterInternalExperience
        var emotion = _emotionPolicy.Evaluate(appraisal);

        Assert.Equal(EmotionType.Frustration, emotion.Type);
        Assert.Equal(EmotionalValence.Negative, emotion.Valence);
        Assert.Equal(0.85, emotion.Intensity);
        Assert.Same(appraisal, emotion.Appraisal);
    }

    [Fact]
    public void PhysicalDeprivation_MapsTo_Concern_WhenModerateIntensity()
    {
        var appraisal = new CharacterAppraisal(AppraisalType.PhysicalDeprivation, AppraisalPolarity.Negative, 0.45, AppraisalSource.Hunger);

        var emotion = _emotionPolicy.Evaluate(appraisal);

        Assert.Equal(EmotionType.Concern, emotion.Type);
        Assert.Equal(EmotionalValence.Negative, emotion.Valence);
    }

    [Fact]
    public void Fatigue_MapsTo_FatigueEmotion()
    {
        var appraisal = new CharacterAppraisal(AppraisalType.Fatigue, AppraisalPolarity.Negative, 0.70, AppraisalSource.Energy);

        var emotion = _emotionPolicy.Evaluate(appraisal);

        Assert.Equal(EmotionType.Fatigue, emotion.Type);
        Assert.Equal(EmotionalValence.Negative, emotion.Valence);
        Assert.Equal(0.70, emotion.Intensity);
    }

    [Fact]
    public void SocialDeprivation_MapsTo_Loneliness()
    {
        var appraisal = new CharacterAppraisal(AppraisalType.SocialDeprivation, AppraisalPolarity.Negative, 0.80, AppraisalSource.SocialNeed);

        var emotion = _emotionPolicy.Evaluate(appraisal);

        Assert.Equal(EmotionType.Loneliness, emotion.Type);
        Assert.Equal(EmotionalValence.Negative, emotion.Valence);
        Assert.Equal(0.80, emotion.Intensity);
    }

    [Fact]
    public void SocialConnection_MapsTo_Joy_WhenHigh_And_Content_WhenModerate()
    {
        var highConn = new CharacterAppraisal(AppraisalType.SocialConnection, AppraisalPolarity.Positive, 0.85, AppraisalSource.SocialNeed);
        var modConn = new CharacterAppraisal(AppraisalType.SocialConnection, AppraisalPolarity.Positive, 0.50, AppraisalSource.SocialNeed);

        var highEmotion = _emotionPolicy.Evaluate(highConn);
        var modEmotion = _emotionPolicy.Evaluate(modConn);

        Assert.Equal(EmotionType.Joy, highEmotion.Type);
        Assert.Equal(EmotionalValence.Positive, highEmotion.Valence);

        Assert.Equal(EmotionType.Content, modEmotion.Type);
        Assert.Equal(EmotionalValence.Positive, modEmotion.Valence);
    }

    [Fact]
    public void StressPressure_MapsTo_Stress_WhenHigh_And_Anxiety_WhenModerate()
    {
        var highStress = new CharacterAppraisal(AppraisalType.StressPressure, AppraisalPolarity.Negative, 0.80, AppraisalSource.Stress);
        var modStress = new CharacterAppraisal(AppraisalType.StressPressure, AppraisalPolarity.Negative, 0.45, AppraisalSource.Stress);

        var highEmotion = _emotionPolicy.Evaluate(highStress);
        var modEmotion = _emotionPolicy.Evaluate(modStress);

        Assert.Equal(EmotionType.Stress, highEmotion.Type);
        Assert.Equal(EmotionalValence.Negative, highEmotion.Valence);

        Assert.Equal(EmotionType.Anxiety, modEmotion.Type);
        Assert.Equal(EmotionalValence.Negative, modEmotion.Valence);
    }

    [Fact]
    public void Discomfort_MapsTo_DiscomfortEmotion()
    {
        var appraisal = new CharacterAppraisal(AppraisalType.Discomfort, AppraisalPolarity.Negative, 0.75, AppraisalSource.Comfort);

        var emotion = _emotionPolicy.Evaluate(appraisal);

        Assert.Equal(EmotionType.Discomfort, emotion.Type);
        Assert.Equal(EmotionalValence.Negative, emotion.Valence);
    }

    [Fact]
    public void NeutralPolarity_OrZeroIntensity_MapsTo_NeutralEmotion()
    {
        var appraisal = new CharacterAppraisal(AppraisalType.PhysicalRestoration, AppraisalPolarity.Neutral, 0.0, AppraisalSource.Hunger);

        var emotion = _emotionPolicy.Evaluate(appraisal);

        Assert.Equal(EmotionType.Neutral, emotion.Type);
        Assert.Equal(EmotionalValence.Neutral, emotion.Valence);
    }

    #endregion

    #region 2. Dominant Emotion Integration Tests (P0 Regression Tests)

    [Fact]
    public void EvaluateDominant_ResolvesPositiveEmotion_WhenPositiveIntensityIsHigherThanNegative()
    {
        // Energy = 90 -> Recovery (Positive 0.90) > StressPressure (Negative 0.25)
        var state = new CharacterStateSnapshot(energy: 90, stress: 25, hunger: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var emotion = _emotionPolicy.EvaluateDominant(exp);

        Assert.Equal(EmotionType.Joy, emotion.Type);
        Assert.Equal(EmotionalValence.Positive, emotion.Valence);
        Assert.Equal(0.90, emotion.Intensity, 2);
    }

    [Fact]
    public void EvaluateDominant_ResolvesNegativeEmotion_WhenNegativeIntensityIsHigherThanPositive()
    {
        // Stress = 85 -> StressPressure (Negative 0.85) > Comfort (Positive 0.50)
        var state = new CharacterStateSnapshot(stress: 85, comfort: 50, energy: 50);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var emotion = _emotionPolicy.EvaluateDominant(exp);

        Assert.Equal(EmotionType.Stress, emotion.Type);
        Assert.Equal(EmotionalValence.Negative, emotion.Valence);
        Assert.Equal(0.85, emotion.Intensity, 2);
    }

    [Fact]
    public void EvaluateDominant_ResolvesFrustration_WhenHungerDominates()
    {
        var state = new CharacterStateSnapshot(hunger: 90, energy: 50, stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var emotion = _emotionPolicy.EvaluateDominant(exp);

        Assert.Equal(EmotionType.Frustration, emotion.Type);
        Assert.Equal(EmotionalValence.Negative, emotion.Valence);
    }

    [Fact]
    public void EvaluateDominant_ResolvesFatigue_WhenEnergyIsExhausted()
    {
        var state = new CharacterStateSnapshot(hunger: 20, energy: 10, stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var emotion = _emotionPolicy.EvaluateDominant(exp);

        Assert.Equal(EmotionType.Fatigue, emotion.Type);
        Assert.Equal(EmotionalValence.Negative, emotion.Valence);
    }

    [Fact]
    public void EvaluateDominant_ResolvesLoneliness_WhenSocialNeedDominates()
    {
        var state = new CharacterStateSnapshot(hunger: 20, energy: 50, socialNeed: 90, stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var emotion = _emotionPolicy.EvaluateDominant(exp);

        Assert.Equal(EmotionType.Loneliness, emotion.Type);
        Assert.Equal(EmotionalValence.Negative, emotion.Valence);
    }

    #endregion

    #region 3. Personality & Double-Modulation Invariant Tests (P0 Regression Tests)

    [Fact]
    public void MoodReactivity_DoesNotScale_NonMoodAppraisals_SuchAsStressOrHunger()
    {
        // Stress appraisal with 0.60 intensity
        var stressAppraisal = new CharacterAppraisal(AppraisalType.StressPressure, AppraisalPolarity.Negative, 0.60, AppraisalSource.Stress);

        // Character has high MoodReactivity (1.8m)
        var highReactivityBlueprint = new CharacterBlueprint(
            Psychology: new PsychologyProfile(MoodReactivity: 1.8m)
        );

        var emotion = _emotionPolicy.Evaluate(stressAppraisal, highReactivityBlueprint);

        // Stress emotion intensity MUST NOT be multiplied by MoodReactivity (remains exactly 0.60)
        Assert.Equal(0.60, emotion.Intensity, 4);
        Assert.Equal(EmotionType.Stress, emotion.Type);
    }

    [Fact]
    public void MoodReactivity_DoesNotDoubleModulate_MoodEmotion()
    {
        // PR39 already scaled MoodPerception.Intensity by MoodReactivity
        var blueprint = new CharacterBlueprint(
            Psychology: new PsychologyProfile(MoodReactivity: 1.5m)
        );

        // State with mood 40 -> PR39 calculates direct 40/100 = 0.40 -> multiplied by MoodReactivity (1.5) = 0.60
        var state = new CharacterStateSnapshot(moodScalar: 40m);
        var exp = _experiencePolicy.Evaluate(state, CreateContext(), blueprint.Psychology);
        Assert.Equal(0.60, exp.Mood.Intensity.Value, 2);

        var moodAppraisal = _appraisalPolicy.EvaluateAll(exp).First(a => a.Source == AppraisalSource.Mood);
        Assert.Equal(0.60, moodAppraisal.Intensity, 2);

        // Emotion evaluation must NOT multiply by MoodReactivity again (no 0.60 * 1.5 = 0.90)
        var emotion = _emotionPolicy.Evaluate(moodAppraisal, blueprint);
        Assert.Equal(0.60, emotion.Intensity, 2);
    }

    #endregion

    #region 4. Invariant Validation Tests

    [Fact]
    public void CharacterEmotion_RejectsInvalidIntensity()
    {
        var appraisal = new CharacterAppraisal(AppraisalType.Fatigue, AppraisalPolarity.Negative, 0.5, AppraisalSource.Energy);

        Assert.Throws<ArgumentException>(() => new CharacterEmotion(EmotionType.Fatigue, double.NaN, EmotionalValence.Negative, appraisal));
        Assert.Throws<ArgumentException>(() => new CharacterEmotion(EmotionType.Fatigue, double.PositiveInfinity, EmotionalValence.Negative, appraisal));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterEmotion(EmotionType.Fatigue, -0.01, EmotionalValence.Negative, appraisal));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterEmotion(EmotionType.Fatigue, 1.01, EmotionalValence.Negative, appraisal));
    }

    [Fact]
    public void CharacterEmotion_RejectsNullAppraisal()
    {
        Assert.Throws<ArgumentNullException>(() => new CharacterEmotion(EmotionType.Joy, 0.5, EmotionalValence.Positive, null!));
    }

    [Fact]
    public void Evaluate_ThrowsArgumentNullException_OnNullInputs()
    {
        Assert.Throws<ArgumentNullException>(() => _emotionPolicy.Evaluate((CharacterAppraisal)null!));
        Assert.Throws<ArgumentNullException>(() => _emotionPolicy.EvaluateDominant(null!));
    }

    #endregion

    #region 5. Determinism & Concurrency Tests

    [Fact]
    public void Evaluate_Is100PercentDeterministic_Over100Evaluations()
    {
        var state = new CharacterStateSnapshot(hunger: 75, energy: 35, stress: 65, version: 4);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());
        var blueprint = new CharacterBlueprint(Psychology: new PsychologyProfile(MoodReactivity: 1.2m));

        var baseline = _emotionPolicy.EvaluateDominant(exp, blueprint);

        for (int i = 0; i < 100; i++)
        {
            var next = _emotionPolicy.EvaluateDominant(exp, blueprint);
            Assert.Equal(baseline, next);
        }
    }

    [Fact]
    public async Task Evaluate_IsSafeForConcurrentExecution_AcrossMultipleWorkers()
    {
        var state = new CharacterStateSnapshot(hunger: 80, energy: 40, socialNeed: 70, version: 15);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());
        var blueprint = new CharacterBlueprint(Psychology: new PsychologyProfile(MoodReactivity: 1.1m));

        var baseline = _emotionPolicy.EvaluateDominant(exp, blueprint);

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            return _emotionPolicy.EvaluateDominant(exp, blueprint);
        }));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(baseline, r));
    }

    #endregion
}
