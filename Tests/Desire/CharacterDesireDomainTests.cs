using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.Desire;

public sealed class CharacterDesireDomainTests
{
    private readonly CharacterInternalExperiencePolicy _experiencePolicy = new();
    private readonly CharacterAppraisalPolicy _appraisalPolicy = new();
    private readonly CharacterEmotionPolicy _emotionPolicy = new();
    private readonly CharacterDesirePolicy _desirePolicy = new();

    private static CharacterPerceptionContext CreateContext(Guid? id = null) =>
        new(new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc), id ?? Guid.NewGuid());

    private CharacterDesireEvaluation RunPipeline(
        CharacterStateSnapshot state,
        CharacterBlueprint? blueprint = null,
        CharacterPerceptionContext? context = null)
    {
        var ctx = context ?? CreateContext();
        var exp = _experiencePolicy.Evaluate(state, ctx, blueprint?.Psychology);
        var appraisal = _appraisalPolicy.Evaluate(exp, blueprint);
        var emotion = _emotionPolicy.Evaluate(appraisal, blueprint);
        return _desirePolicy.Evaluate(exp, appraisal, emotion, blueprint);
    }

    #region 1. Value Object & Invariant Validation Tests

    [Fact]
    public void CharacterMotivation_RejectsInvalidIntensity()
    {
        Assert.Throws<ArgumentException>(() => new CharacterMotivation(MotivationType.HungerDriven, double.NaN, DesireSource.Hunger));
        Assert.Throws<ArgumentException>(() => new CharacterMotivation(MotivationType.HungerDriven, double.PositiveInfinity, DesireSource.Hunger));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterMotivation(MotivationType.HungerDriven, -0.01, DesireSource.Hunger));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterMotivation(MotivationType.HungerDriven, 1.01, DesireSource.Hunger));
    }

    [Fact]
    public void CharacterDesire_RejectsInvalidIntensity_OrNullMotivation()
    {
        var motivation = new CharacterMotivation(MotivationType.HungerDriven, 0.5, DesireSource.Hunger);

        Assert.Throws<ArgumentNullException>(() => new CharacterDesire(DesireType.NeedFood, 0.5, DesireSource.Hunger, null!));
        Assert.Throws<ArgumentException>(() => new CharacterDesire(DesireType.NeedFood, double.NaN, DesireSource.Hunger, motivation));
        Assert.Throws<ArgumentException>(() => new CharacterDesire(DesireType.NeedFood, double.PositiveInfinity, DesireSource.Hunger, motivation));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterDesire(DesireType.NeedFood, -0.01, DesireSource.Hunger, motivation));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterDesire(DesireType.NeedFood, 1.01, DesireSource.Hunger, motivation));
    }

    [Fact]
    public void CharacterDesireEvaluation_RejectsInvalidArguments()
    {
        var motivation = new CharacterMotivation(MotivationType.HungerDriven, 0.5, DesireSource.Hunger);
        var desire = new CharacterDesire(DesireType.NeedFood, 0.5, DesireSource.Hunger, motivation);

        Assert.Throws<ArgumentException>(() => new CharacterDesireEvaluation(Guid.Empty, 1, new[] { desire }, desire));
        Assert.Throws<ArgumentNullException>(() => new CharacterDesireEvaluation(Guid.NewGuid(), 1, null!, desire));
        Assert.Throws<ArgumentNullException>(() => new CharacterDesireEvaluation(Guid.NewGuid(), 1, new[] { desire }, null!));
        Assert.Throws<ArgumentException>(() => new CharacterDesireEvaluation(Guid.NewGuid(), 1, Array.Empty<CharacterDesire>(), desire));
    }

    [Fact]
    public void Evaluate_ThrowsArgumentNullException_WhenInputsAreNull()
    {
        var state = new CharacterStateSnapshot();
        var exp = _experiencePolicy.Evaluate(state, CreateContext());
        var appraisal = _appraisalPolicy.Evaluate(exp);
        var emotion = _emotionPolicy.Evaluate(appraisal);

        Assert.Throws<ArgumentNullException>(() => _desirePolicy.Evaluate(null!, appraisal, emotion));
        Assert.Throws<ArgumentNullException>(() => _desirePolicy.Evaluate(exp, null!, emotion));
        Assert.Throws<ArgumentNullException>(() => _desirePolicy.Evaluate(exp, appraisal, null!));
    }

    #endregion

    #region 2. Desire & Motivation Mapping Tests

    [Fact]
    public void Hunger_MapsTo_NeedFood_And_HungerDrivenMotivation()
    {
        var state = new CharacterStateSnapshot(hunger: 85, energy: 60, stress: 10);
        var result = RunPipeline(state);

        var foodDesire = result.Desires.First(d => d.Type == DesireType.NeedFood);

        Assert.Equal(DesireSource.Hunger, foodDesire.Source);
        Assert.Equal(MotivationType.HungerDriven, foodDesire.Motivation.Type);
        Assert.Equal(foodDesire.Intensity, foodDesire.Motivation.Intensity);
        Assert.True(foodDesire.Intensity >= 0.85);
    }

    [Fact]
    public void LowEnergy_MapsTo_NeedRest_And_RestorationDrivenMotivation()
    {
        // Energy = 15 -> Fatigue intensity = 0.85
        var state = new CharacterStateSnapshot(energy: 15, hunger: 10, stress: 10);
        var result = RunPipeline(state);

        var restDesire = result.Desires.First(d => d.Type == DesireType.NeedRest);

        Assert.Equal(DesireSource.Energy, restDesire.Source);
        Assert.Equal(MotivationType.RestorationDriven, restDesire.Motivation.Type);
        Assert.Equal(restDesire.Intensity, restDesire.Motivation.Intensity);
        Assert.True(restDesire.Intensity >= 0.85);
    }

    [Fact]
    public void HighStress_MapsTo_NeedReduceStress_And_StressReliefDrivenMotivation()
    {
        var state = new CharacterStateSnapshot(stress: 80, energy: 60, hunger: 10);
        var result = RunPipeline(state);

        var stressDesire = result.Desires.First(d => d.Type == DesireType.NeedReduceStress);

        Assert.Equal(DesireSource.Stress, stressDesire.Source);
        Assert.Equal(MotivationType.StressReliefDriven, stressDesire.Motivation.Type);
        Assert.Equal(stressDesire.Intensity, stressDesire.Motivation.Intensity);
        Assert.True(stressDesire.Intensity >= 0.80);
    }

    [Fact]
    public void HighSocialNeed_MapsTo_NeedSocialConnection_And_ConnectionDrivenMotivation()
    {
        var state = new CharacterStateSnapshot(socialNeed: 80, energy: 60, hunger: 10);
        var result = RunPipeline(state);

        var socialDesire = result.Desires.First(d => d.Type == DesireType.NeedSocialConnection);

        Assert.Equal(DesireSource.SocialNeed, socialDesire.Source);
        Assert.Equal(MotivationType.ConnectionDriven, socialDesire.Motivation.Type);
        Assert.Equal(socialDesire.Intensity, socialDesire.Motivation.Intensity);
        Assert.True(socialDesire.Intensity >= 0.80);
    }

    [Fact]
    public void LowComfort_MapsTo_NeedComfort_And_ComfortDrivenMotivation()
    {
        // Comfort = 20 -> Discomfort intensity = 0.80
        var state = new CharacterStateSnapshot(comfort: 20, energy: 60, hunger: 10);
        var result = RunPipeline(state);

        var comfortDesire = result.Desires.First(d => d.Type == DesireType.NeedComfort);

        Assert.Equal(DesireSource.Comfort, comfortDesire.Source);
        Assert.Equal(MotivationType.ComfortDriven, comfortDesire.Motivation.Type);
        Assert.Equal(comfortDesire.Intensity, comfortDesire.Motivation.Intensity);
        Assert.True(comfortDesire.Intensity >= 0.80);
    }

    #endregion

    #region 3. Emotion Influence Tests (Amplification & Suppression)

    [Fact]
    public void Frustration_Amplifies_FoodDesire_WhenHungerIsPresent()
    {
        var state = new CharacterStateSnapshot(hunger: 60, energy: 60, stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var hungerAppraisal = new CharacterAppraisal(AppraisalType.PhysicalDeprivation, AppraisalPolarity.Negative, 0.60, AppraisalSource.Hunger);
        var frustrationEmotion = new CharacterEmotion(EmotionType.Frustration, 0.80, EmotionalValence.Negative, hungerAppraisal);
        var calmEmotion = new CharacterEmotion(EmotionType.Neutral, 0.0, EmotionalValence.Neutral, hungerAppraisal);

        var amplifiedResult = _desirePolicy.Evaluate(exp, hungerAppraisal, frustrationEmotion);
        var unamplifiedResult = _desirePolicy.Evaluate(exp, hungerAppraisal, calmEmotion);

        var amplifiedFood = amplifiedResult.Desires.First(d => d.Type == DesireType.NeedFood);
        var unamplifiedFood = unamplifiedResult.Desires.First(d => d.Type == DesireType.NeedFood);

        Assert.True(amplifiedFood.Intensity > unamplifiedFood.Intensity);
        Assert.Equal(0.60 + (0.15 * 0.80), amplifiedFood.Intensity, 3);
    }

    [Fact]
    public void Loneliness_Amplifies_SocialDesire_WhenSocialNeedIsPresent()
    {
        var state = new CharacterStateSnapshot(socialNeed: 50, energy: 60, stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var socialAppraisal = new CharacterAppraisal(AppraisalType.SocialDeprivation, AppraisalPolarity.Negative, 0.50, AppraisalSource.SocialNeed);
        var lonelinessEmotion = new CharacterEmotion(EmotionType.Loneliness, 0.70, EmotionalValence.Negative, socialAppraisal);
        var neutralEmotion = new CharacterEmotion(EmotionType.Neutral, 0.0, EmotionalValence.Neutral, socialAppraisal);

        var amplified = _desirePolicy.Evaluate(exp, socialAppraisal, lonelinessEmotion);
        var baseResult = _desirePolicy.Evaluate(exp, socialAppraisal, neutralEmotion);

        var amplifiedSocial = amplified.Desires.First(d => d.Type == DesireType.NeedSocialConnection);
        var baseSocial = baseResult.Desires.First(d => d.Type == DesireType.NeedSocialConnection);

        Assert.True(amplifiedSocial.Intensity > baseSocial.Intensity);
        Assert.Equal(0.50 + (0.15 * 0.70), amplifiedSocial.Intensity, 3);
    }

    [Fact]
    public void Fatigue_Amplifies_RestDesire_WhenEnergyIsLow()
    {
        // Energy = 40 -> Fatigue base = 0.60
        var state = new CharacterStateSnapshot(energy: 40, hunger: 10, stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var fatigueAppraisal = new CharacterAppraisal(AppraisalType.Fatigue, AppraisalPolarity.Negative, 0.60, AppraisalSource.Energy);
        var fatigueEmotion = new CharacterEmotion(EmotionType.Fatigue, 0.60, EmotionalValence.Negative, fatigueAppraisal);
        var neutralEmotion = new CharacterEmotion(EmotionType.Neutral, 0.0, EmotionalValence.Neutral, fatigueAppraisal);

        var amplified = _desirePolicy.Evaluate(exp, fatigueAppraisal, fatigueEmotion);
        var baseline = _desirePolicy.Evaluate(exp, fatigueAppraisal, neutralEmotion);

        var amplifiedRest = amplified.Desires.First(d => d.Type == DesireType.NeedRest);
        var baseRest = baseline.Desires.First(d => d.Type == DesireType.NeedRest);

        Assert.True(amplifiedRest.Intensity > baseRest.Intensity);
        Assert.Equal(0.60 + (0.15 * 0.60), amplifiedRest.Intensity, 3);
    }

    [Fact]
    public void Discomfort_Amplifies_ComfortDesire_WhenComfortIsLow()
    {
        // Comfort = 50 -> Discomfort base = 0.50
        var state = new CharacterStateSnapshot(comfort: 50, energy: 60, hunger: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var discomfortAppraisal = new CharacterAppraisal(AppraisalType.Discomfort, AppraisalPolarity.Negative, 0.50, AppraisalSource.Comfort);
        var discomfortEmotion = new CharacterEmotion(EmotionType.Discomfort, 0.60, EmotionalValence.Negative, discomfortAppraisal);
        var neutralEmotion = new CharacterEmotion(EmotionType.Neutral, 0.0, EmotionalValence.Neutral, discomfortAppraisal);

        var amplified = _desirePolicy.Evaluate(exp, discomfortAppraisal, discomfortEmotion);
        var baseline = _desirePolicy.Evaluate(exp, discomfortAppraisal, neutralEmotion);

        var amplifiedComfort = amplified.Desires.First(d => d.Type == DesireType.NeedComfort);
        var baseComfort = baseline.Desires.First(d => d.Type == DesireType.NeedComfort);

        Assert.True(amplifiedComfort.Intensity > baseComfort.Intensity);
    }

    [Fact]
    public void Relief_Suppresses_Urgency_OfRelatedDesires()
    {
        var state = new CharacterStateSnapshot(hunger: 60, energy: 60, stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        var recoveryAppraisal = new CharacterAppraisal(AppraisalType.Recovery, AppraisalPolarity.Positive, 0.60, AppraisalSource.Energy);
        var reliefEmotion = new CharacterEmotion(EmotionType.Relief, 0.80, EmotionalValence.Positive, recoveryAppraisal);
        var neutralEmotion = new CharacterEmotion(EmotionType.Neutral, 0.0, EmotionalValence.Neutral, recoveryAppraisal);

        var suppressed = _desirePolicy.Evaluate(exp, recoveryAppraisal, reliefEmotion);
        var baseline = _desirePolicy.Evaluate(exp, recoveryAppraisal, neutralEmotion);

        var suppressedFood = suppressed.Desires.First(d => d.Type == DesireType.NeedFood);
        var baseFood = baseline.Desires.First(d => d.Type == DesireType.NeedFood);

        Assert.True(suppressedFood.Intensity < baseFood.Intensity);
        Assert.Equal(0.60 - (0.15 * 0.80), suppressedFood.Intensity, 3);
    }

    [Fact]
    public void Emotion_DoesNotCreate_PhantomDesire_WhenBaseNeedIsZero()
    {
        // Hunger = 0 (satisfied) -> base need is 0.0
        var state = new CharacterStateSnapshot(hunger: 0, energy: 80, stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());

        // Even with Frustration 1.0, NeedFood MUST remain strictly 0.0
        var appraisal = new CharacterAppraisal(AppraisalType.PhysicalDeprivation, AppraisalPolarity.Negative, 0.0, AppraisalSource.Hunger);
        var frustration = new CharacterEmotion(EmotionType.Frustration, 1.0, EmotionalValence.Negative, appraisal);

        var result = _desirePolicy.Evaluate(exp, appraisal, frustration);
        var foodDesire = result.Desires.First(d => d.Type == DesireType.NeedFood);

        Assert.Equal(0.0, foodDesire.Intensity);
    }

    #endregion

    #region 4. Dominant Desire & Deterministic Precedence Tests

    [Fact]
    public void DominantDesire_SelectsHighestIntensity_RegardlessOfNeedCategory()
    {
        // Hunger (90 -> ~0.90) dominates Rest (energy 80 -> rest 0.20)
        var state = new CharacterStateSnapshot(hunger: 90, energy: 80, stress: 20);
        var result = RunPipeline(state);

        Assert.Equal(DesireType.NeedFood, result.DominantDesire.Type);
        Assert.Equal(MotivationType.HungerDriven, result.DominantMotivation.Type);
        Assert.True(result.DominantDesire.Intensity >= 0.90);
    }

    [Fact]
    public void DominantDesire_ResolvesTies_ByPrecedence_FoodOverRest()
    {
        // Equal intensity: Hunger 80 (0.80) vs Energy 20 (Fatigue 0.80)
        // Precedence: NeedFood > NeedRest
        var state = new CharacterStateSnapshot(hunger: 80, energy: 20, stress: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());
        var neutralAppraisal = new CharacterAppraisal(AppraisalType.PhysicalDeprivation, AppraisalPolarity.Negative, 0.80, AppraisalSource.Hunger);
        var neutralEmotion = new CharacterEmotion(EmotionType.Neutral, 0.0, EmotionalValence.Neutral, neutralAppraisal);

        var result = _desirePolicy.Evaluate(exp, neutralAppraisal, neutralEmotion);

        var food = result.Desires.First(d => d.Type == DesireType.NeedFood);
        var rest = result.Desires.First(d => d.Type == DesireType.NeedRest);

        Assert.Equal(food.Intensity, rest.Intensity);
        Assert.Equal(DesireType.NeedFood, result.DominantDesire.Type);
    }

    [Fact]
    public void DominantDesire_ResolvesTies_ByPrecedence_RestOverStress()
    {
        // Equal intensity: Energy 20 (Fatigue 0.80) vs Stress 80 (0.80)
        // Precedence: NeedRest > NeedReduceStress
        var state = new CharacterStateSnapshot(energy: 20, stress: 80, hunger: 10);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());
        var neutralAppraisal = new CharacterAppraisal(AppraisalType.Fatigue, AppraisalPolarity.Negative, 0.80, AppraisalSource.Energy);
        var neutralEmotion = new CharacterEmotion(EmotionType.Neutral, 0.0, EmotionalValence.Neutral, neutralAppraisal);

        var result = _desirePolicy.Evaluate(exp, neutralAppraisal, neutralEmotion);

        var rest = result.Desires.First(d => d.Type == DesireType.NeedRest);
        var stress = result.Desires.First(d => d.Type == DesireType.NeedReduceStress);

        Assert.Equal(rest.Intensity, stress.Intensity);
        Assert.Equal(DesireType.NeedRest, result.DominantDesire.Type);
    }

    [Fact]
    public void DominantDesire_ResolvesTies_ByPrecedence_StressOverSocial()
    {
        // Equal intensity: Stress 80 (0.80) vs SocialNeed 80 (0.80)
        // Precedence: NeedReduceStress > NeedSocialConnection
        var state = new CharacterStateSnapshot(stress: 80, socialNeed: 80, hunger: 10, energy: 60);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());
        var neutralAppraisal = new CharacterAppraisal(AppraisalType.StressPressure, AppraisalPolarity.Negative, 0.80, AppraisalSource.Stress);
        var neutralEmotion = new CharacterEmotion(EmotionType.Neutral, 0.0, EmotionalValence.Neutral, neutralAppraisal);

        var result = _desirePolicy.Evaluate(exp, neutralAppraisal, neutralEmotion);

        Assert.Equal(DesireType.NeedReduceStress, result.DominantDesire.Type);
    }

    [Fact]
    public void DominantDesire_ResolvesTies_ByPrecedence_SocialOverComfort()
    {
        // Equal intensity: SocialNeed 80 (0.80) vs Comfort 20 (Discomfort 0.80)
        // Precedence: NeedSocialConnection > NeedComfort
        var state = new CharacterStateSnapshot(socialNeed: 80, comfort: 20, hunger: 10, energy: 60);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());
        var neutralAppraisal = new CharacterAppraisal(AppraisalType.SocialDeprivation, AppraisalPolarity.Negative, 0.80, AppraisalSource.SocialNeed);
        var neutralEmotion = new CharacterEmotion(EmotionType.Neutral, 0.0, EmotionalValence.Neutral, neutralAppraisal);

        var result = _desirePolicy.Evaluate(exp, neutralAppraisal, neutralEmotion);

        Assert.Equal(DesireType.NeedSocialConnection, result.DominantDesire.Type);
    }

    #endregion

    #region 5. Determinism, Concurrency & Pipeline Regression Tests

    [Fact]
    public void Evaluate_Is100PercentDeterministic_Over100Evaluations()
    {
        var state = new CharacterStateSnapshot(hunger: 70, energy: 35, stress: 55, socialNeed: 65, version: 7);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());
        var appraisal = _appraisalPolicy.Evaluate(exp);
        var emotion = _emotionPolicy.Evaluate(appraisal);

        var baseline = _desirePolicy.Evaluate(exp, appraisal, emotion);

        for (int i = 0; i < 100; i++)
        {
            var next = _desirePolicy.Evaluate(exp, appraisal, emotion);
            Assert.Equal(baseline.DominantDesire.Type, next.DominantDesire.Type);
            Assert.Equal(baseline.DominantDesire.Intensity, next.DominantDesire.Intensity);
            Assert.Equal(baseline.DominantMotivation.Type, next.DominantMotivation.Type);
            Assert.Equal(baseline.Desires.Count, next.Desires.Count);
        }
    }

    [Fact]
    public async Task Evaluate_IsSafeForConcurrentExecution_AcrossMultipleWorkers()
    {
        var state = new CharacterStateSnapshot(hunger: 75, energy: 40, stress: 50, version: 12);
        var exp = _experiencePolicy.Evaluate(state, CreateContext());
        var appraisal = _appraisalPolicy.Evaluate(exp);
        var emotion = _emotionPolicy.Evaluate(appraisal);

        var baseline = _desirePolicy.Evaluate(exp, appraisal, emotion);

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            return _desirePolicy.Evaluate(exp, appraisal, emotion);
        }));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r =>
        {
            Assert.Equal(baseline.DominantDesire.Type, r.DominantDesire.Type);
            Assert.Equal(baseline.DominantDesire.Intensity, r.DominantDesire.Intensity);
        });
    }

    [Fact]
    public void StateAndPreviousLayers_AreNotMutated_DuringDesireEvaluation()
    {
        var state = new CharacterStateSnapshot(hunger: 70, energy: 30, stress: 40, version: 15);
        var context = CreateContext();
        var exp = _experiencePolicy.Evaluate(state, context);
        var appraisal = _appraisalPolicy.Evaluate(exp);
        var emotion = _emotionPolicy.Evaluate(appraisal);

        var desireResult = _desirePolicy.Evaluate(exp, appraisal, emotion);

        // State remains intact
        Assert.Equal(70, state.Hunger);
        Assert.Equal(30, state.Energy);
        Assert.Equal(40, state.Stress);
        Assert.Equal(15, state.Version);

        // Experience remains intact
        Assert.Equal(70, exp.Hunger.RawValue);
        Assert.Equal(15, exp.StateVersion);

        // Result preservation
        Assert.Equal(exp.CharacterId, desireResult.CharacterId);
        Assert.Equal(exp.StateVersion, desireResult.StateVersion);
    }

    #endregion
}
