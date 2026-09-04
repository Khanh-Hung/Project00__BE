using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.Intent;

public sealed class CharacterIntentDomainTests
{
    private readonly CharacterInternalExperiencePolicy _experiencePolicy = new();
    private readonly CharacterAppraisalPolicy _appraisalPolicy = new();
    private readonly CharacterEmotionPolicy _emotionPolicy = new();
    private readonly CharacterDesirePolicy _desirePolicy = new();
    private readonly CharacterIntentPolicy _intentPolicy = new();

    private static readonly DateTimeOffset FixedEvaluatedAt =
        new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);

    private static CharacterIntentContext CreateContext() =>
        new(FixedEvaluatedAt);

    private static CharacterPerceptionContext CreatePerceptionContext(Guid? id = null) =>
        new(new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc), id ?? Guid.NewGuid());

    private CharacterIntentEvaluation RunFullPipeline(
        CharacterStateSnapshot state,
        CharacterBlueprint? blueprint = null,
        CharacterIntentContext? intentContext = null)
    {
        var ctx = CreatePerceptionContext();
        var exp = _experiencePolicy.Evaluate(state, ctx, blueprint?.Psychology);
        var appraisal = _appraisalPolicy.Evaluate(exp, blueprint);
        var emotion = _emotionPolicy.Evaluate(appraisal, blueprint);
        var desire = _desirePolicy.Evaluate(exp, appraisal, emotion);
        return _intentPolicy.Evaluate(desire, intentContext ?? CreateContext());
    }

    private static CharacterDesireEvaluation CreateDesireEvaluation(
        DesireType dominantType,
        double intensity,
        MotivationType motivationType,
        int stateVersion = 1,
        Guid? characterId = null)
    {
        var motivation = new CharacterMotivation(motivationType, intensity, DesireSource.Hunger);
        var dominantDesire = new CharacterDesire(dominantType, intensity, DesireSource.Hunger, motivation);
        var desires = new List<CharacterDesire> { dominantDesire };

        return new CharacterDesireEvaluation(
            characterId ?? Guid.NewGuid(),
            stateVersion,
            desires,
            dominantDesire
        );
    }

    #region 1. Domain Validation Tests

    [Fact]
    public void CharacterIntent_RejectsInvalidIntensity()
    {
        Assert.Throws<ArgumentException>(() => new CharacterIntent(IntentType.SeekFood, double.NaN, DesireType.NeedFood, MotivationType.HungerDriven, 1));
        Assert.Throws<ArgumentException>(() => new CharacterIntent(IntentType.SeekFood, double.PositiveInfinity, DesireType.NeedFood, MotivationType.HungerDriven, 1));
        Assert.Throws<ArgumentException>(() => new CharacterIntent(IntentType.SeekFood, double.NegativeInfinity, DesireType.NeedFood, MotivationType.HungerDriven, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterIntent(IntentType.SeekFood, -0.01, DesireType.NeedFood, MotivationType.HungerDriven, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterIntent(IntentType.SeekFood, 1.01, DesireType.NeedFood, MotivationType.HungerDriven, 1));
    }

    [Fact]
    public void CharacterIntent_RejectsNegativeStateVersion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterIntent(IntentType.SeekFood, 0.5, DesireType.NeedFood, MotivationType.HungerDriven, -1));
    }

    [Fact]
    public void CharacterIntent_AcceptsValidBoundaries()
    {
        var zero = new CharacterIntent(IntentType.SeekFood, 0.0, DesireType.NeedFood, MotivationType.HungerDriven, 0);
        var one = new CharacterIntent(IntentType.SeekFood, 1.0, DesireType.NeedFood, MotivationType.HungerDriven, 42);

        Assert.Equal(0.0, zero.Intensity);
        Assert.Equal(1.0, one.Intensity);
        Assert.Equal(42, one.StateVersion);
    }

    [Fact]
    public void CharacterIntentEvaluation_RejectsInvalidArguments()
    {
        var intent = new CharacterIntent(IntentType.SeekFood, 0.5, DesireType.NeedFood, MotivationType.HungerDriven, 1);

        Assert.Throws<ArgumentException>(() => new CharacterIntentEvaluation(Guid.Empty, 1, intent, FixedEvaluatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterIntentEvaluation(Guid.NewGuid(), -1, intent, FixedEvaluatedAt));
    }

    [Fact]
    public void Evaluate_ThrowsArgumentNullException_OnNullInputs()
    {
        var desire = CreateDesireEvaluation(DesireType.NeedFood, 0.8, MotivationType.HungerDriven);
        var context = CreateContext();

        Assert.Throws<ArgumentNullException>(() => _intentPolicy.Evaluate(null!, context));
        Assert.Throws<ArgumentNullException>(() => _intentPolicy.Evaluate(desire, null!));
    }

    #endregion

    #region 2. Explicit Desire -> Intent Mapping Tests

    [Theory]
    [InlineData(DesireType.NeedFood, IntentType.SeekFood)]
    [InlineData(DesireType.NeedRest, IntentType.SeekRest)]
    [InlineData(DesireType.NeedReduceStress, IntentType.ReduceStress)]
    [InlineData(DesireType.NeedSocialConnection, IntentType.SeekSocialConnection)]
    [InlineData(DesireType.NeedComfort, IntentType.SeekComfort)]
    [InlineData(DesireType.NeedSafety, IntentType.SeekSafety)]
    public void Desire_MapsExactly_ToExpectedIntentType(DesireType desireType, IntentType expectedIntentType)
    {
        var desire = CreateDesireEvaluation(desireType, 0.75, MotivationType.HungerDriven);
        var evaluation = _intentPolicy.Evaluate(desire, CreateContext());

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(expectedIntentType, evaluation.Intent.Type);
        Assert.Equal(desireType, evaluation.Intent.SourceDesire);
    }

    #endregion

    #region 3. Intensity, Motivation & StateVersion Preservation Tests

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(1.0)]
    public void Intensity_IsPreservedExactly_WithoutRounding(double expectedIntensity)
    {
        var desire = CreateDesireEvaluation(DesireType.NeedFood, expectedIntensity, MotivationType.HungerDriven);
        var evaluation = _intentPolicy.Evaluate(desire, CreateContext());

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(expectedIntensity, evaluation.Intent.Intensity);
    }

    [Fact]
    public void Motivation_IsPreservedDirectly_FromAuthoritativeDesire()
    {
        var desire = CreateDesireEvaluation(DesireType.NeedFood, 0.85, MotivationType.HungerDriven);
        var evaluation = _intentPolicy.Evaluate(desire, CreateContext());

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(MotivationType.HungerDriven, evaluation.Intent.Motivation);
    }

    [Fact]
    public void StateVersion_And_CharacterId_ArePreserved_AcrossEvaluations()
    {
        var charId = Guid.NewGuid();
        const int stateVersion = 42;

        var desire = CreateDesireEvaluation(DesireType.NeedRest, 0.70, MotivationType.RestorationDriven, stateVersion, charId);
        var evaluation = _intentPolicy.Evaluate(desire, CreateContext());

        Assert.Equal(charId, evaluation.CharacterId);
        Assert.Equal(stateVersion, evaluation.StateVersion);

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(stateVersion, evaluation.Intent.StateVersion);
        Assert.Equal(FixedEvaluatedAt, evaluation.EvaluatedAtUtc);
    }

    #endregion

    #region 4. No-Desire & Invariant Tests

    [Fact]
    public void NoDesire_ProducesNullIntent_WithoutFakeActionOrIdle()
    {
        // Dominant desire with 0 intensity represents no meaningful desire
        var desire = CreateDesireEvaluation(DesireType.NeedFood, 0.0, MotivationType.HungerDriven);
        var evaluation = _intentPolicy.Evaluate(desire, CreateContext());

        // Must be null, NOT a fake intent
        Assert.Null(evaluation.Intent);
        Assert.Equal(FixedEvaluatedAt, evaluation.EvaluatedAtUtc);
    }

    [Fact]
    public void Emotion_CannotCreatePhantomIntent_WhenDesiresAreZero()
    {
        // Fully satisfied character: Hunger = 0, Energy = 100, Stress = 0, SocialNeed = 0, Comfort = 100
        var satisfiedState = new CharacterStateSnapshot(hunger: 0, energy: 100, stress: 0, socialNeed: 0, comfort: 100);
        var evaluation = RunFullPipeline(satisfiedState);

        // When base desires are 0, intent must strictly be null
        Assert.Null(evaluation.Intent);
    }

    #endregion

    #region 5. Dominant Desire Integration & Deterministic Tie-Breaking Tests

    [Fact]
    public void Intent_FollowsDominantDesire_WhenHungerDominatesRest()
    {
        // Hunger 90 > Energy 70 (Fatigue 0.30)
        var state = new CharacterStateSnapshot(hunger: 90, energy: 70, stress: 10);
        var evaluation = RunFullPipeline(state);

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(IntentType.SeekFood, evaluation.Intent.Type);
        Assert.Equal(MotivationType.HungerDriven, evaluation.Intent.Motivation);
        Assert.True(evaluation.Intent.Intensity >= 0.90);
    }

    [Fact]
    public void Intent_ResolvesTies_ByPrecedence_FoodOverRest()
    {
        // Equal intensity: Hunger 80 (0.80) vs Energy 20 (Fatigue 0.80)
        // PR41 Precedence: NeedFood > NeedRest
        var state = new CharacterStateSnapshot(hunger: 80, energy: 20, stress: 10);
        var evaluation = RunFullPipeline(state);

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(IntentType.SeekFood, evaluation.Intent.Type);
        Assert.Equal(DesireType.NeedFood, evaluation.Intent.SourceDesire);
    }

    [Fact]
    public void Intent_ResolvesTies_ByPrecedence_RestOverStress()
    {
        // Equal intensity: Energy 20 (Fatigue 0.80) vs Stress 80 (0.80)
        // PR41 Precedence: NeedRest > NeedReduceStress
        var state = new CharacterStateSnapshot(energy: 20, stress: 80, hunger: 10);
        var exp = _experiencePolicy.Evaluate(state, CreatePerceptionContext());
        var neutralAppraisal = new CharacterAppraisal(AppraisalType.Fatigue, AppraisalPolarity.Negative, 0.80, AppraisalSource.Energy);
        var neutralEmotion = new CharacterEmotion(EmotionType.Neutral, 0.0, EmotionalValence.Neutral, neutralAppraisal);

        var desireEvaluation = _desirePolicy.Evaluate(exp, neutralAppraisal, neutralEmotion);
        var evaluation = _intentPolicy.Evaluate(desireEvaluation, CreateContext());

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(IntentType.SeekRest, evaluation.Intent.Type);
        Assert.Equal(DesireType.NeedRest, evaluation.Intent.SourceDesire);
    }

    #endregion

    #region 6. Determinism, Concurrency & Immutability Tests

    [Fact]
    public void Evaluate_Is100PercentDeterministic_Over100Evaluations()
    {
        var state = new CharacterStateSnapshot(hunger: 75, energy: 30, stress: 45, version: 5);
        var baseline = RunFullPipeline(state);

        for (int i = 0; i < 100; i++)
        {
            var next = RunFullPipeline(state);
            Assert.Equal(baseline.Intent?.Type, next.Intent?.Type);
            Assert.Equal(baseline.Intent?.Intensity, next.Intent?.Intensity);
            Assert.Equal(baseline.Intent?.Motivation, next.Intent?.Motivation);
            Assert.Equal(baseline.StateVersion, next.StateVersion);
        }
    }

    [Fact]
    public async Task Evaluate_IsSafeForConcurrentExecution_AcrossMultipleWorkers()
    {
        var state = new CharacterStateSnapshot(hunger: 65, energy: 35, stress: 55, version: 10);
        var baseline = RunFullPipeline(state);

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            return RunFullPipeline(state);
        }));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r =>
        {
            Assert.Equal(baseline.Intent?.Type, r.Intent?.Type);
            Assert.Equal(baseline.Intent?.Intensity, r.Intent?.Intensity);
            Assert.Equal(baseline.Intent?.Motivation, r.Intent?.Motivation);
            Assert.Equal(baseline.StateVersion, r.StateVersion);
        });
    }

    [Fact]
    public void UpstreamObjects_AreNeverMutated_DuringIntentEvaluation()
    {
        var state = new CharacterStateSnapshot(hunger: 70, energy: 40, stress: 30, version: 12);
        var ctx = CreatePerceptionContext();
        var exp = _experiencePolicy.Evaluate(state, ctx);
        var appraisal = _appraisalPolicy.Evaluate(exp);
        var emotion = _emotionPolicy.Evaluate(appraisal);
        var desire = _desirePolicy.Evaluate(exp, appraisal, emotion);

        var intentEvaluation = _intentPolicy.Evaluate(desire, CreateContext());

        // State remains intact
        Assert.Equal(70, state.Hunger);
        Assert.Equal(40, state.Energy);
        Assert.Equal(12, state.Version);

        // Desire remains intact
        Assert.Equal(12, desire.StateVersion);
        Assert.Equal(desire.DominantDesire.Intensity, intentEvaluation.Intent?.Intensity);
    }

    #endregion
}
