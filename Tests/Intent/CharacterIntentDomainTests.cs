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

    private static (DesireSource Source, MotivationType Motivation) GetDefaultGroundedSemantics(DesireType type) =>
        type switch
        {
            DesireType.NeedFood => (DesireSource.Hunger, MotivationType.HungerDriven),
            DesireType.NeedRest => (DesireSource.Energy, MotivationType.RestorationDriven),
            DesireType.NeedReduceStress => (DesireSource.Stress, MotivationType.StressReliefDriven),
            DesireType.NeedSocialConnection => (DesireSource.SocialNeed, MotivationType.ConnectionDriven),
            DesireType.NeedComfort => (DesireSource.Comfort, MotivationType.ComfortDriven),
            DesireType.NeedSafety => (DesireSource.Stress, MotivationType.SafetyDriven),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

    private static CharacterDesire CreateDesire(
        DesireType type,
        double intensity,
        DesireSource? source = null,
        MotivationType? motivationType = null)
    {
        var (defaultSource, defaultMotivation) = GetDefaultGroundedSemantics(type);
        var actualSource = source ?? defaultSource;
        var actualMotivation = motivationType ?? defaultMotivation;
        var motivation = new CharacterMotivation(actualMotivation, intensity, actualSource);
        return new CharacterDesire(type, intensity, actualSource, motivation);
    }

    private static CharacterDesireEvaluation CreateDesireEvaluation(
        DesireType dominantType,
        double intensity,
        MotivationType? motivationType = null,
        DesireSource? source = null,
        int stateVersion = 1,
        Guid? characterId = null)
    {
        var dominantDesire = CreateDesire(dominantType, intensity, source, motivationType);
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
        var desire = CreateDesireEvaluation(DesireType.NeedFood, 0.8);
        var context = CreateContext();

        Assert.Throws<ArgumentNullException>(() => _intentPolicy.Evaluate(null!, context));
        Assert.Throws<ArgumentNullException>(() => _intentPolicy.Evaluate(desire, null!));
    }

    #endregion

    #region 2. Explicit Desire -> Intent Mapping Tests

    [Theory]
    [InlineData(DesireType.NeedFood, IntentType.SeekFood, MotivationType.HungerDriven)]
    [InlineData(DesireType.NeedRest, IntentType.SeekRest, MotivationType.RestorationDriven)]
    [InlineData(DesireType.NeedReduceStress, IntentType.ReduceStress, MotivationType.StressReliefDriven)]
    [InlineData(DesireType.NeedSocialConnection, IntentType.SeekSocialConnection, MotivationType.ConnectionDriven)]
    [InlineData(DesireType.NeedComfort, IntentType.SeekComfort, MotivationType.ComfortDriven)]
    [InlineData(DesireType.NeedSafety, IntentType.SeekSafety, MotivationType.SafetyDriven)]
    public void Desire_MapsExactly_ToExpectedIntentType(
        DesireType desireType,
        IntentType expectedIntentType,
        MotivationType expectedMotivationType)
    {
        var desire = CreateDesireEvaluation(desireType, 0.75);
        var evaluation = _intentPolicy.Evaluate(desire, CreateContext());

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(expectedIntentType, evaluation.Intent.Type);
        Assert.Equal(desireType, evaluation.Intent.SourceDesire);
        Assert.Equal(expectedMotivationType, evaluation.Intent.Motivation);
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
        var desire = CreateDesireEvaluation(DesireType.NeedFood, expectedIntensity);
        var evaluation = _intentPolicy.Evaluate(desire, CreateContext());

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(expectedIntensity, evaluation.Intent.Intensity);
    }

    [Fact]
    public void Motivation_IsPreservedDirectly_FromAuthoritativeDesire()
    {
        var desire = CreateDesireEvaluation(DesireType.NeedFood, 0.85);
        var evaluation = _intentPolicy.Evaluate(desire, CreateContext());

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(MotivationType.HungerDriven, evaluation.Intent.Motivation);
    }

    [Fact]
    public void StateVersion_And_CharacterId_ArePreserved_AcrossEvaluations()
    {
        var charId = Guid.NewGuid();
        const int stateVersion = 42;

        var desire = CreateDesireEvaluation(DesireType.NeedRest, 0.70, stateVersion: stateVersion, characterId: charId);
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
        var desire = CreateDesireEvaluation(DesireType.NeedFood, 0.0);
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

    #region 5. Authoritative Dominant Desire Respect & Pipeline Integration Tests

    [Fact]
    public void Intent_FaithfullyRespects_AuthoritativeDominantDesire_DirectEvaluation()
    {
        // When PR41 outputs an authoritative DominantDesire, PR42 must directly map that DominantDesire
        // into the Intent without attempting to re-evaluate, re-score, or re-rank the desires list.
        var restDesire = CreateDesire(DesireType.NeedRest, 0.80);
        var foodDesire = CreateDesire(DesireType.NeedFood, 0.80);

        // Explicitly set NeedFood as authoritative DominantDesire from PR41
        var desireEvaluation = new CharacterDesireEvaluation(
            Guid.NewGuid(),
            stateVersion: 3,
            desires: new List<CharacterDesire> { restDesire, foodDesire },
            dominantDesire: foodDesire
        );

        var evaluation = _intentPolicy.Evaluate(desireEvaluation, CreateContext());

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(IntentType.SeekFood, evaluation.Intent.Type);
        Assert.Equal(DesireType.NeedFood, evaluation.Intent.SourceDesire);
        Assert.Equal(MotivationType.HungerDriven, evaluation.Intent.Motivation);
        Assert.Equal(0.80, evaluation.Intent.Intensity);
    }

    [Fact]
    public void PipelineIntegration_IntentFollowsDominantDesire_WhenHungerDominatesRest()
    {
        // In the full upstream pipeline, PR41 selects NeedFood when Hunger (0.90) > Fatigue (0.30).
        // PR42 faithfully transforms that authoritative dominant desire into SeekFood intent.
        var state = new CharacterStateSnapshot(hunger: 90, energy: 70, stress: 10);
        var evaluation = RunFullPipeline(state);

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(IntentType.SeekFood, evaluation.Intent.Type);
        Assert.Equal(MotivationType.HungerDriven, evaluation.Intent.Motivation);
        Assert.True(evaluation.Intent.Intensity >= 0.90);
    }

    [Fact]
    public void PipelineIntegration_IntentReflects_UpstreamAuthoritativeTieBreak_FoodOverRest()
    {
        // Equal intensity: Hunger 80 (0.80) vs Energy 20 (Fatigue 0.80).
        // Upstream PR41 is responsible for resolving the tie (Precedence: NeedFood > NeedRest).
        // PR42 does not resolve ties; it faithfully maps PR41's authoritative dominant desire to SeekFood.
        var state = new CharacterStateSnapshot(hunger: 80, energy: 20, stress: 10);
        var evaluation = RunFullPipeline(state);

        Assert.NotNull(evaluation.Intent);
        Assert.Equal(IntentType.SeekFood, evaluation.Intent.Type);
        Assert.Equal(DesireType.NeedFood, evaluation.Intent.SourceDesire);
    }

    [Fact]
    public void PipelineIntegration_IntentReflects_UpstreamAuthoritativeTieBreak_RestOverStress()
    {
        // Equal intensity: Energy 20 (Fatigue 0.80) vs Stress 80 (0.80).
        // Upstream PR41 resolves the tie by precedence (NeedRest > NeedReduceStress).
        // PR42 faithfully maps PR41's authoritative dominant desire to SeekRest.
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
