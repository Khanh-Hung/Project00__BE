using System;
using System.Collections.Generic;
using Application.Contracts.Goals;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Services.Goals;
using Xunit;

namespace Tests.GoalSystem;

public sealed class CharacterGoalPolicyTests
{
    private readonly CharacterGoalPolicy _policy = new();

    [Fact]
    public void GoalPolicy_CreatesGoalForSupportedDesire()
    {
        var charId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var motivation = new CharacterMotivation(MotivationType.ConnectionDriven, 0.85, DesireSource.SocialNeed);
        var desire = new CharacterDesire(DesireType.NeedSocialConnection, 0.85, DesireSource.SocialNeed, motivation);
        var desireEval = new CharacterDesireEvaluation(charId, 1, new[] { desire }, desire);

        var decision = _policy.Evaluate(charId, desireEval, Array.Empty<CharacterGoal>(), now);

        Assert.Equal(GoalPolicyAction.CreateNew, decision.Action);
        Assert.NotNull(decision.Candidate);
        Assert.Equal("BuildRelationship", decision.Candidate.GoalKey);
        Assert.Equal(CharacterGoalType.Relationship, decision.Candidate.GoalType);
    }

    [Fact]
    public void GoalPolicy_ReusesExistingCompatibleGoal()
    {
        var charId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var existingGoal = new CharacterGoal(
            charId,
            "BuildRelationship",
            goalType: CharacterGoalType.Relationship,
            initialStatus: CharacterGoalStatus.Active);

        var motivation = new CharacterMotivation(MotivationType.ConnectionDriven, 0.85, DesireSource.SocialNeed);
        var desire = new CharacterDesire(DesireType.NeedSocialConnection, 0.85, DesireSource.SocialNeed, motivation);
        var desireEval = new CharacterDesireEvaluation(charId, 1, new[] { desire }, desire);

        var decision = _policy.Evaluate(charId, desireEval, new[] { existingGoal }, now);

        Assert.Equal(GoalPolicyAction.ReuseExisting, decision.Action);
        Assert.NotNull(decision.SelectedGoal);
        Assert.Equal(existingGoal.Id, decision.SelectedGoal.Id);
    }

    [Fact]
    public void GoalPolicy_DoesNotCreateDuplicateGoalAcrossRepeatedCycles()
    {
        var charId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var motivation = new CharacterMotivation(MotivationType.ConnectionDriven, 0.85, DesireSource.SocialNeed);
        var desire = new CharacterDesire(DesireType.NeedSocialConnection, 0.85, DesireSource.SocialNeed, motivation);
        var desireEval = new CharacterDesireEvaluation(charId, 1, new[] { desire }, desire);

        var activeGoals = new List<CharacterGoal>();

        // Cycle 1: Spawns new goal candidate
        var decision1 = _policy.Evaluate(charId, desireEval, activeGoals, now);
        Assert.Equal(GoalPolicyAction.CreateNew, decision1.Action);

        var createdGoal = CharacterGoal.Create(
            charId, decision1.Candidate!.GoalKey, decision1.Candidate.GoalType, decision1.Candidate.Priority);
        activeGoals.Add(createdGoal);

        // Cycle 2: Reuses created goal
        var decision2 = _policy.Evaluate(charId, desireEval, activeGoals, now);
        Assert.Equal(GoalPolicyAction.ReuseExisting, decision2.Action);
        Assert.Equal(createdGoal.Id, decision2.SelectedGoal!.Id);

        // Cycle 3: Reuses created goal again
        var decision3 = _policy.Evaluate(charId, desireEval, activeGoals, now);
        Assert.Equal(GoalPolicyAction.ReuseExisting, decision3.Action);
        Assert.Equal(createdGoal.Id, decision3.SelectedGoal!.Id);
    }

    [Fact]
    public void GoalPolicy_IsDeterministic()
    {
        var charId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var motivation = new CharacterMotivation(MotivationType.RestorationDriven, 0.9, DesireSource.Energy);
        var desire = new CharacterDesire(DesireType.NeedRest, 0.9, DesireSource.Energy, motivation);
        var desireEval = new CharacterDesireEvaluation(charId, 1, new[] { desire }, desire);

        var decision1 = _policy.Evaluate(charId, desireEval, Array.Empty<CharacterGoal>(), now);
        var decision2 = _policy.Evaluate(charId, desireEval, Array.Empty<CharacterGoal>(), now);

        Assert.Equal(decision1.Action, decision2.Action);
        Assert.Equal(decision1.Candidate?.GoalKey, decision2.Candidate?.GoalKey);
        Assert.Equal(decision1.Candidate?.Priority, decision2.Candidate?.Priority);
    }

    [Fact]
    public void GoalPolicy_DoesNotCreateGoalForUnsupportedDesire()
    {
        var charId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Desire with 0 intensity produces no goal
        var zeroMotivation = new CharacterMotivation(MotivationType.ConnectionDriven, 0.0, DesireSource.SocialNeed);
        var zeroDesire = new CharacterDesire(DesireType.NeedSocialConnection, 0.0, DesireSource.SocialNeed, zeroMotivation);
        var zeroDesireEval = new CharacterDesireEvaluation(charId, 1, new[] { zeroDesire }, zeroDesire);

        var decisionZero = _policy.Evaluate(charId, zeroDesireEval, Array.Empty<CharacterGoal>(), now);
        Assert.Equal(GoalPolicyAction.None, decisionZero.Action);

        // Unsupported desire type (NeedSafety) produces no autonomous goal in MVP
        var safetyMotivation = new CharacterMotivation(MotivationType.SafetyDriven, 0.8, DesireSource.Comfort);
        var safetyDesire = new CharacterDesire(DesireType.NeedSafety, 0.8, DesireSource.Comfort, safetyMotivation);
        var safetyDesireEval = new CharacterDesireEvaluation(charId, 1, new[] { safetyDesire }, safetyDesire);

        var decisionUnsupported = _policy.Evaluate(charId, safetyDesireEval, Array.Empty<CharacterGoal>(), now);
        Assert.Equal(GoalPolicyAction.None, decisionUnsupported.Action);
        Assert.Null(decisionUnsupported.SelectedGoal);
        Assert.Null(decisionUnsupported.Candidate);
    }

    [Fact]
    public void AlignedAction_ProducesGoalProgress()
    {
        // Aligned pairs must produce positive progress contribution
        Assert.True(_policy.EvaluateActionProgress("BuildRelationship", "Socialize") > 0);
        Assert.True(_policy.EvaluateActionProgress("BuildRelationship", "Chat") > 0);
        Assert.True(_policy.EvaluateActionProgress("BuildRelationship", "Greet") > 0);
        Assert.True(_policy.EvaluateActionProgress("Rest", "Rest") > 0);
        Assert.True(_policy.EvaluateActionProgress("Eat", "Eat") > 0);
        Assert.True(_policy.EvaluateActionProgress("ReduceStress", "Relax") > 0);
    }

    [Fact]
    public void UnrelatedAction_DoesNotProduceGoalProgress()
    {
        // Unrelated actions must produce 0 contribution
        Assert.Equal(0, _policy.EvaluateActionProgress("BuildRelationship", "Eat"));
        Assert.Equal(0, _policy.EvaluateActionProgress("BuildRelationship", "Rest"));
        Assert.Equal(0, _policy.EvaluateActionProgress("Rest", "Socialize"));
        Assert.Equal(0, _policy.EvaluateActionProgress("Eat", "Sleep"));
        Assert.Equal(0, _policy.EvaluateActionProgress("UnknownGoal", "Socialize"));
        Assert.Equal(0, _policy.EvaluateActionProgress("BuildRelationship", "UnknownAction"));
    }

    [Fact]
    public void SameGoalSameAction_IsDeterministic()
    {
        for (int i = 0; i < 5; i++)
        {
            var delta1 = _policy.EvaluateActionProgress("BuildRelationship", "Socialize");
            var delta2 = _policy.EvaluateActionProgress("BuildRelationship", "Socialize");
            Assert.Equal(delta1, delta2);
            Assert.Equal(25, delta1);
        }
    }
}
