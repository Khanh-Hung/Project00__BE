using System;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.SocialPresence;

public sealed class SocialBehaviorPolicyTests
{
    private readonly SocialBehaviorPolicy _policy = new();
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

    private static CharacterSocialPresenceContext CreatePresence(
        Guid charId,
        SocialPresenceStatus status = SocialPresenceStatus.Active,
        LifeActivityType activity = LifeActivityType.Idle,
        RelationshipTargetType? targetType = null,
        Guid? targetId = null)
    {
        return new CharacterSocialPresenceContext(
            PresenceId: Guid.NewGuid(),
            CharacterId: charId,
            Status: status,
            CurrentActivityType: activity,
            Visibility: SocialPresenceVisibility.Public,
            StartedAtUtc: FixedNow,
            UpdatedAtUtc: FixedNow,
            TargetType: targetType,
            TargetId: targetId,
            Version: 1
        );
    }

    [Fact]
    public void Evaluate_EmptyCharacterId_ThrowsArgumentException()
    {
        var presence = CreatePresence(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() =>
            _policy.Evaluate(Guid.Empty, presence, null, null, null));
    }

    [Fact]
    public void Evaluate_NullPresence_ReturnsNull()
    {
        var result = _policy.Evaluate(Guid.NewGuid(), null, null, null, null);
        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_OfflinePresence_SuppressesSocialAction()
    {
        var charId = Guid.NewGuid();
        var presence = CreatePresence(charId, status: SocialPresenceStatus.Offline);

        var decision = _policy.Evaluate(charId, presence, null, null, null);

        Assert.NotNull(decision);
        Assert.False(decision.ShouldAct);
        Assert.Equal(ActionType.Socialize, decision.ProposedAction);
        Assert.Contains("offline", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_AwayPresence_SuppressesSocialAction()
    {
        var charId = Guid.NewGuid();
        var presence = CreatePresence(charId, status: SocialPresenceStatus.Away);

        var decision = _policy.Evaluate(charId, presence, null, null, null);

        Assert.NotNull(decision);
        Assert.False(decision.ShouldAct);
        Assert.Equal(ActionType.Socialize, decision.ProposedAction);
        Assert.Contains("away", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ActivePresenceWithSocialGoalAndTarget_ProposesSocializeWithBonus()
    {
        var charId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var presence = CreatePresence(charId, status: SocialPresenceStatus.Active);
        var goal = new CharacterGoalContext(
            GoalId: Guid.NewGuid(),
            GoalKey: "Socialize",
            Status: CharacterGoalStatus.Active,
            Priority: 1,
            Progress: 10
        );
        var relationship = new CharacterRelationshipContext(
            TargetId: targetId,
            TargetType: RelationshipTargetType.User,
            RelationshipType: RelationshipType.Friend,
            Trust: 50,
            Affection: 50,
            Familiarity: 50
        );

        var decision = _policy.Evaluate(charId, presence, relationship, goal, null);

        Assert.NotNull(decision);
        Assert.True(decision.ShouldAct);
        Assert.Equal(ActionType.Socialize, decision.ProposedAction);
        Assert.Equal(0.10, decision.IntensityBonus);
        Assert.Equal(RelationshipTargetType.User, decision.TargetType);
        Assert.Equal(targetId, decision.TargetId);
        Assert.Contains("target", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ActivePresenceWithBuildRelationshipGoal_ProposesSocialize()
    {
        var charId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var presence = CreatePresence(charId, status: SocialPresenceStatus.Active);
        var goal = new CharacterGoalContext(
            GoalId: Guid.NewGuid(),
            GoalKey: "BuildRelationship",
            Status: CharacterGoalStatus.Active,
            Priority: 1,
            Progress: 10
        );
        var relationship = new CharacterRelationshipContext(
            TargetId: targetId,
            TargetType: RelationshipTargetType.Character,
            RelationshipType: RelationshipType.Acquaintance,
            Trust: 30,
            Affection: 30,
            Familiarity: 20
        );

        var decision = _policy.Evaluate(charId, presence, relationship, goal, null);

        Assert.NotNull(decision);
        Assert.True(decision.ShouldAct);
        Assert.Equal(ActionType.Socialize, decision.ProposedAction);
        Assert.Equal(0.10, decision.IntensityBonus);
        Assert.Equal(RelationshipTargetType.Character, decision.TargetType);
        Assert.Equal(targetId, decision.TargetId);
    }

    [Fact]
    public void Evaluate_SocialGoalWithHighConfidencePersonality_ReceivesHigherBonus()
    {
        var charId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var presence = CreatePresence(charId, status: SocialPresenceStatus.Active);
        var goal = new CharacterGoalContext(
            GoalId: Guid.NewGuid(),
            GoalKey: "Socialize",
            Status: CharacterGoalStatus.Active,
            Priority: 1,
            Progress: 10
        );
        var relationship = new CharacterRelationshipContext(
            TargetId: targetId,
            TargetType: RelationshipTargetType.User,
            RelationshipType: RelationshipType.Friend,
            Trust: 50,
            Affection: 50,
            Familiarity: 50
        );
        var personality = new CharacterPersonalitySnapshot(
            CharacterId: charId,
            Version: 1,
            Warmth: 75,
            Openness: 50,
            Assertiveness: 50,
            Conscientiousness: 50,
            SocialConfidence: 75,
            TrustDisposition: 50,
            EmotionalStability: 50,
            SnapshotAtUtc: FixedNow
        );

        var decision = _policy.Evaluate(charId, presence, relationship, goal, personality);

        Assert.NotNull(decision);
        Assert.True(decision.ShouldAct);
        Assert.Equal(0.15, decision.IntensityBonus);
    }

    [Fact]
    public void Evaluate_ActivePresenceWithSocialGoalWithoutTarget_ProposesSocializeWithNullTarget()
    {
        var charId = Guid.NewGuid();
        var presence = CreatePresence(charId, status: SocialPresenceStatus.Active);
        var goal = new CharacterGoalContext(
            GoalId: Guid.NewGuid(),
            GoalKey: "Socialize",
            Status: CharacterGoalStatus.Active,
            Priority: 1,
            Progress: 10
        );

        var decision = _policy.Evaluate(charId, presence, null, goal, null);

        Assert.NotNull(decision);
        Assert.True(decision.ShouldAct);
        Assert.Equal(ActionType.Socialize, decision.ProposedAction);
        Assert.Null(decision.TargetType);
        Assert.Null(decision.TargetId);
    }

    [Fact]
    public void Evaluate_OngoingSocializeActivity_ReinforcesWithSmallBonus()
    {
        var charId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var presence = CreatePresence(
            charId,
            status: SocialPresenceStatus.Active,
            activity: LifeActivityType.Socialize,
            targetType: RelationshipTargetType.User,
            targetId: targetId
        );

        var decision = _policy.Evaluate(charId, presence, null, null, null);

        Assert.NotNull(decision);
        Assert.True(decision.ShouldAct);
        Assert.Equal(ActionType.Socialize, decision.ProposedAction);
        Assert.Equal(0.05, decision.IntensityBonus);
        Assert.Equal(RelationshipTargetType.User, decision.TargetType);
        Assert.Equal(targetId, decision.TargetId);
        Assert.Contains("ongoing", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_NoSocialGoalAndIdleActivity_ReturnsNull()
    {
        var charId = Guid.NewGuid();
        var presence = CreatePresence(charId, status: SocialPresenceStatus.Active, activity: LifeActivityType.Idle);

        var decision = _policy.Evaluate(charId, presence, null, null, null);

        Assert.Null(decision);
    }

    [Fact]
    public void Evaluate_IsPureAndDeterministicAcross100Iterations()
    {
        var charId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var presence = CreatePresence(charId, status: SocialPresenceStatus.Active);
        var goal = new CharacterGoalContext(
            GoalId: Guid.NewGuid(),
            GoalKey: "Socialize",
            Status: CharacterGoalStatus.Active,
            Priority: 1,
            Progress: 10
        );
        var relationship = new CharacterRelationshipContext(
            TargetId: targetId,
            TargetType: RelationshipTargetType.User,
            RelationshipType: RelationshipType.Friend,
            Trust: 50,
            Affection: 50,
            Familiarity: 50
        );

        var first = _policy.Evaluate(charId, presence, relationship, goal, null);

        for (int i = 0; i < 100; i++)
        {
            var current = _policy.Evaluate(charId, presence, relationship, goal, null);
            Assert.Equal(first, current);
        }
    }
}
