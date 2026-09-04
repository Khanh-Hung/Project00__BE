using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.ActionProposal;

public sealed class CharacterActionProposalDomainTests
{
    private readonly CharacterInternalExperiencePolicy _experiencePolicy = new();
    private readonly CharacterAppraisalPolicy _appraisalPolicy = new();
    private readonly CharacterEmotionPolicy _emotionPolicy = new();
    private readonly CharacterDesirePolicy _desirePolicy = new();
    private readonly CharacterIntentPolicy _intentPolicy = new();
    private readonly CharacterActionProposalPolicy _actionPolicy = new();

    private static readonly DateTimeOffset FixedEvaluatedAt =
        new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private static CharacterActionProposalContext CreateContext(DateTimeOffset? evaluatedAt = null) =>
        new(evaluatedAt ?? FixedEvaluatedAt);

    private static CharacterIntentContext CreateIntentContext() =>
        new(FixedEvaluatedAt);

    private static CharacterPerceptionContext CreatePerceptionContext(Guid? id = null) =>
        new(new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc), id ?? Guid.NewGuid());

    private CharacterActionProposalEvaluation RunFullPipeline(
        CharacterStateSnapshot state,
        CharacterBlueprint? blueprint = null,
        CharacterActionProposalContext? actionContext = null)
    {
        var perceptionCtx = CreatePerceptionContext();
        var exp = _experiencePolicy.Evaluate(state, perceptionCtx, blueprint?.Psychology);
        var appraisal = _appraisalPolicy.Evaluate(exp, blueprint);
        var emotion = _emotionPolicy.Evaluate(appraisal, blueprint);
        var desire = _desirePolicy.Evaluate(exp, appraisal, emotion);
        var intent = _intentPolicy.Evaluate(desire, CreateIntentContext());
        return _actionPolicy.Evaluate(intent, actionContext ?? CreateContext());
    }

    private static CharacterIntentEvaluation CreateIntentEvaluation(
        IntentType intentType,
        double intensity,
        DesireType sourceDesire = DesireType.NeedFood,
        MotivationType motivationType = MotivationType.HungerDriven,
        int stateVersion = 1,
        Guid? characterId = null)
    {
        var intent = new CharacterIntent(
            intentType,
            intensity,
            sourceDesire,
            motivationType,
            stateVersion
        );

        return new CharacterIntentEvaluation(
            characterId ?? Guid.NewGuid(),
            stateVersion,
            intent,
            FixedEvaluatedAt
        );
    }

    #region 1. Domain Validation Tests

    [Fact]
    public void CharacterActionProposal_RejectsInvalidIntensity()
    {
        Assert.Throws<ArgumentException>(() => new CharacterActionProposal(ActionType.Eat, double.NaN, IntentType.SeekFood, MotivationType.HungerDriven, 1));
        Assert.Throws<ArgumentException>(() => new CharacterActionProposal(ActionType.Eat, double.PositiveInfinity, IntentType.SeekFood, MotivationType.HungerDriven, 1));
        Assert.Throws<ArgumentException>(() => new CharacterActionProposal(ActionType.Eat, double.NegativeInfinity, IntentType.SeekFood, MotivationType.HungerDriven, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterActionProposal(ActionType.Eat, -0.01, IntentType.SeekFood, MotivationType.HungerDriven, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterActionProposal(ActionType.Eat, 1.01, IntentType.SeekFood, MotivationType.HungerDriven, 1));
    }

    [Fact]
    public void CharacterActionProposal_RejectsNegativeStateVersion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterActionProposal(ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, -1));
    }

    [Fact]
    public void CharacterActionProposal_AcceptsValidBoundaries()
    {
        var zero = new CharacterActionProposal(ActionType.Eat, 0.0, IntentType.SeekFood, MotivationType.HungerDriven, 0);
        var one = new CharacterActionProposal(ActionType.Eat, 1.0, IntentType.SeekFood, MotivationType.RestorationDriven, 42);

        Assert.Equal(0.0, zero.Intensity);
        Assert.Equal(MotivationType.HungerDriven, zero.Motivation);
        Assert.Equal(0, zero.StateVersion);

        Assert.Equal(1.0, one.Intensity);
        Assert.Equal(MotivationType.RestorationDriven, one.Motivation);
        Assert.Equal(42, one.StateVersion);
    }

    [Fact]
    public void CharacterActionProposalEvaluation_RejectsInvalidArguments()
    {
        var proposal = new CharacterActionProposal(ActionType.Eat, 0.5, IntentType.SeekFood, MotivationType.HungerDriven, 1);

        Assert.Throws<ArgumentException>(() => new CharacterActionProposalEvaluation(Guid.Empty, 1, proposal, FixedEvaluatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterActionProposalEvaluation(Guid.NewGuid(), -1, proposal, FixedEvaluatedAt));
    }

    [Fact]
    public void Evaluate_ThrowsArgumentNullException_OnNullInputs()
    {
        var intent = CreateIntentEvaluation(IntentType.SeekFood, 0.8);
        var context = CreateContext();

        Assert.Throws<ArgumentNullException>(() => _actionPolicy.Evaluate(null!, context));
        Assert.Throws<ArgumentNullException>(() => _actionPolicy.Evaluate(intent, null!));
    }

    #endregion

    #region 2. Intent -> Action Mapping Tests

    [Theory]
    [InlineData(IntentType.SeekFood, ActionType.Eat, MotivationType.HungerDriven)]
    [InlineData(IntentType.SeekRest, ActionType.Rest, MotivationType.RestorationDriven)]
    [InlineData(IntentType.ReduceStress, ActionType.ReduceStress, MotivationType.StressReliefDriven)]
    [InlineData(IntentType.SeekSocialConnection, ActionType.Socialize, MotivationType.ConnectionDriven)]
    [InlineData(IntentType.SeekComfort, ActionType.SeekComfort, MotivationType.ComfortDriven)]
    [InlineData(IntentType.SeekSafety, ActionType.SeekSafety, MotivationType.SafetyDriven)]
    public void Intent_MapsExactly_ToExpectedActionType(
        IntentType intentType,
        ActionType expectedActionType,
        MotivationType expectedMotivation)
    {
        var intentEval = CreateIntentEvaluation(intentType, 0.75, motivationType: expectedMotivation);
        var evaluation = _actionPolicy.Evaluate(intentEval, CreateContext());

        Assert.NotNull(evaluation.Proposal);
        Assert.Equal(expectedActionType, evaluation.Proposal.Type);
        Assert.Equal(intentType, evaluation.Proposal.SourceIntent);
        Assert.Equal(expectedMotivation, evaluation.Proposal.Motivation);
        Assert.Equal(intentEval.Intent!.Motivation, evaluation.Proposal.Motivation);
    }

    #endregion

    #region 3. Intensity, Motivation & Provenance Preservation Tests

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.731927)]
    [InlineData(0.8)]
    [InlineData(1.0)]
    public void Intensity_IsPreservedExactly_WithoutRoundingOrScaling(double expectedIntensity)
    {
        var intentEval = CreateIntentEvaluation(IntentType.SeekFood, expectedIntensity);
        var evaluation = _actionPolicy.Evaluate(intentEval, CreateContext());

        Assert.NotNull(evaluation.Proposal);
        Assert.Equal(expectedIntensity, evaluation.Proposal.Intensity);
    }

    [Theory]
    [InlineData(MotivationType.HungerDriven)]
    [InlineData(MotivationType.RestorationDriven)]
    [InlineData(MotivationType.StressReliefDriven)]
    [InlineData(MotivationType.ConnectionDriven)]
    [InlineData(MotivationType.ComfortDriven)]
    [InlineData(MotivationType.SafetyDriven)]
    public void Motivation_PreservesMotivationTypeProvenance_FromIntent(MotivationType expectedMotivation)
    {
        var intentEval = CreateIntentEvaluation(IntentType.SeekFood, 0.75, motivationType: expectedMotivation);
        var evaluation = _actionPolicy.Evaluate(intentEval, CreateContext());

        Assert.NotNull(evaluation.Proposal);
        Assert.Equal(expectedMotivation, evaluation.Proposal.Motivation);
        Assert.Equal(intentEval.Intent!.Motivation, evaluation.Proposal.Motivation);
    }

    [Fact]
    public void StateVersion_And_CharacterId_ArePreserved_AcrossEvaluations()
    {
        var charId = Guid.NewGuid();
        const int stateVersion = 42;

        var intentEval = CreateIntentEvaluation(
            IntentType.SeekRest,
            0.70,
            DesireType.NeedRest,
            MotivationType.RestorationDriven,
            stateVersion,
            charId);

        var evaluation = _actionPolicy.Evaluate(intentEval, CreateContext());

        Assert.Equal(charId, evaluation.CharacterId);
        Assert.Equal(stateVersion, evaluation.StateVersion);

        Assert.NotNull(evaluation.Proposal);
        Assert.Equal(stateVersion, evaluation.Proposal.StateVersion);
        Assert.Equal(FixedEvaluatedAt, evaluation.EvaluatedAtUtc);
    }

    #endregion

    #region 4. Null Intent & No Phantom Action Tests

    [Fact]
    public void NullIntent_ProducesNullProposal_WithoutFakeActionOrIdle()
    {
        var nullIntentEval = new CharacterIntentEvaluation(
            Guid.NewGuid(),
            stateVersion: 1,
            intent: null,
            evaluatedAtUtc: FixedEvaluatedAt
        );

        var evaluation = _actionPolicy.Evaluate(nullIntentEval, CreateContext());

        Assert.Null(evaluation.Proposal);
        Assert.Equal(FixedEvaluatedAt, evaluation.EvaluatedAtUtc);
        Assert.Equal(1, evaluation.StateVersion);
    }

    [Fact]
    public void SatisfiedCharacter_ProducesNullActionProposal_AcrossEntirePipeline()
    {
        // Fully satisfied character: Hunger = 0, Energy = 100, Stress = 0, SocialNeed = 0, Comfort = 100
        var satisfiedState = new CharacterStateSnapshot(hunger: 0, energy: 100, stress: 0, socialNeed: 0, comfort: 100);
        var evaluation = RunFullPipeline(satisfiedState);

        // With zero base desires, intent is null, and proposal must strictly be null
        Assert.Null(evaluation.Proposal);
    }

    #endregion

    #region 5. Authoritative Intent Tests

    [Fact]
    public void Policy_FaithfullyRespects_AuthoritativeIntent_WithoutRecalculation()
    {
        // Even if an external observer might expect food, if authoritative intent is SeekRest,
        // PR43 strictly produces ActionType.Rest without inspecting or recalculating upstream state.
        var intentEval = CreateIntentEvaluation(
            IntentType.SeekRest,
            0.88,
            DesireType.NeedRest,
            MotivationType.RestorationDriven,
            stateVersion: 5);

        var evaluation = _actionPolicy.Evaluate(intentEval, CreateContext());

        Assert.NotNull(evaluation.Proposal);
        Assert.Equal(ActionType.Rest, evaluation.Proposal.Type);
        Assert.Equal(IntentType.SeekRest, evaluation.Proposal.SourceIntent);
        Assert.Equal(0.88, evaluation.Proposal.Intensity);
        Assert.Equal(MotivationType.RestorationDriven, evaluation.Proposal.Motivation);
        Assert.Equal(intentEval.Intent!.Motivation, evaluation.Proposal.Motivation);
        Assert.Equal(5, evaluation.Proposal.StateVersion);
    }

    #endregion

    #region 6. Full Pipeline Integration Tests

    [Fact]
    public void PipelineIntegration_PropagatesHungerToEatAction_PreservingIntensityAndMotivation()
    {
        // State: Hunger 90, Energy 70, Stress 10
        // Pipeline: Hunger 90 -> NeedFood -> SeekFood -> Eat
        var state = new CharacterStateSnapshot(hunger: 90, energy: 70, stress: 10, version: 7);
        var evaluation = RunFullPipeline(state);

        Assert.NotNull(evaluation.Proposal);
        Assert.Equal(ActionType.Eat, evaluation.Proposal.Type);
        Assert.Equal(IntentType.SeekFood, evaluation.Proposal.SourceIntent);
        Assert.True(evaluation.Proposal.Intensity >= 0.90);
        Assert.Equal(MotivationType.HungerDriven, evaluation.Proposal.Motivation);
        Assert.Equal(7, evaluation.StateVersion);
        Assert.Equal(7, evaluation.Proposal.StateVersion);
    }

    #endregion

    #region 7. Determinism, Concurrency, Context & Immutability Tests

    [Fact]
    public void Evaluate_Is100PercentDeterministic_Over100Evaluations()
    {
        var state = new CharacterStateSnapshot(hunger: 75, energy: 30, stress: 45, version: 5);
        var baseline = RunFullPipeline(state);

        for (int i = 0; i < 100; i++)
        {
            var next = RunFullPipeline(state);
            Assert.Equal(baseline.Proposal?.Type, next.Proposal?.Type);
            Assert.Equal(baseline.Proposal?.Intensity, next.Proposal?.Intensity);
            Assert.Equal(baseline.Proposal?.Motivation, next.Proposal?.Motivation);
            Assert.Equal(baseline.Proposal?.SourceIntent, next.Proposal?.SourceIntent);
            Assert.Equal(baseline.StateVersion, next.StateVersion);
            Assert.Equal(baseline.Proposal?.StateVersion, next.Proposal?.StateVersion);
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
            Assert.Equal(baseline.Proposal?.Type, r.Proposal?.Type);
            Assert.Equal(baseline.Proposal?.Intensity, r.Proposal?.Intensity);
            Assert.Equal(baseline.Proposal?.Motivation, r.Proposal?.Motivation);
            Assert.Equal(baseline.Proposal?.SourceIntent, r.Proposal?.SourceIntent);
            Assert.Equal(baseline.StateVersion, r.StateVersion);
            Assert.Equal(baseline.Proposal?.StateVersion, r.Proposal?.StateVersion);
        });
    }

    [Fact]
    public void UpstreamObjects_AreNeverMutated_DuringActionProposalEvaluation()
    {
        var state = new CharacterStateSnapshot(hunger: 70, energy: 40, stress: 30, version: 12);
        var perceptionCtx = CreatePerceptionContext();
        var exp = _experiencePolicy.Evaluate(state, perceptionCtx);
        var appraisal = _appraisalPolicy.Evaluate(exp);
        var emotion = _emotionPolicy.Evaluate(appraisal);
        var desire = _desirePolicy.Evaluate(exp, appraisal, emotion);
        var intentEval = _intentPolicy.Evaluate(desire, CreateIntentContext());

        var initialIntent = intentEval.Intent;
        Assert.NotNull(initialIntent);

        var proposalEval = _actionPolicy.Evaluate(intentEval, CreateContext());

        // Assert intent evaluation is completely unmutated
        Assert.Same(initialIntent, intentEval.Intent);
        Assert.Equal(initialIntent.Intensity, proposalEval.Proposal?.Intensity);
        Assert.Equal(initialIntent.Motivation, proposalEval.Proposal?.Motivation);
        Assert.Equal(initialIntent.Type, proposalEval.Proposal?.SourceIntent);
        Assert.Equal(initialIntent.StateVersion, proposalEval.Proposal?.StateVersion);
        Assert.Equal(12, state.Version);
    }

    [Fact]
    public void DifferentContextTimestamps_OnlyAffectEvaluatedAtUtc_PreservingProposal()
    {
        var intentEval = CreateIntentEvaluation(IntentType.SeekFood, 0.65, stateVersion: 8);

        var time1 = new DateTimeOffset(2026, 9, 4, 11, 0, 0, TimeSpan.Zero);
        var time2 = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        var eval1 = _actionPolicy.Evaluate(intentEval, CreateContext(time1));
        var eval2 = _actionPolicy.Evaluate(intentEval, CreateContext(time2));

        Assert.Equal(time1, eval1.EvaluatedAtUtc);
        Assert.Equal(time2, eval2.EvaluatedAtUtc);

        Assert.Equal(eval1.Proposal?.Type, eval2.Proposal?.Type);
        Assert.Equal(eval1.Proposal?.Intensity, eval2.Proposal?.Intensity);
        Assert.Equal(eval1.Proposal?.Motivation, eval2.Proposal?.Motivation);
        Assert.Equal(eval1.Proposal?.SourceIntent, eval2.Proposal?.SourceIntent);
        Assert.Equal(eval1.Proposal?.StateVersion, eval2.Proposal?.StateVersion);
        Assert.Equal(eval1.CharacterId, eval2.CharacterId);
        Assert.Equal(eval1.StateVersion, eval2.StateVersion);
    }

    #endregion
}
