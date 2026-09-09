using System;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Xunit;

namespace Tests.SocialPresence;

public sealed class CharacterSocialPresenceDomainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_WithValidArguments_InitializesCorrectly()
    {
        var charId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var presence = new CharacterSocialPresence(
            characterId: charId,
            status: SocialPresenceStatus.Active,
            currentActivityType: LifeActivityType.Socialize,
            visibility: SocialPresenceVisibility.Public,
            startedAtUtc: FixedNow,
            updatedAtUtc: FixedNow,
            targetType: RelationshipTargetType.User,
            targetId: targetId,
            version: 1
        );

        Assert.NotEqual(Guid.Empty, presence.Id);
        Assert.Equal(charId, presence.CharacterId);
        Assert.Equal(SocialPresenceStatus.Active, presence.Status);
        Assert.Equal(LifeActivityType.Socialize, presence.CurrentActivityType);
        Assert.Equal(SocialPresenceVisibility.Public, presence.Visibility);
        Assert.Equal(RelationshipTargetType.User, presence.TargetType);
        Assert.Equal(targetId, presence.TargetId);
        Assert.Equal(FixedNow, presence.StartedAtUtc);
        Assert.Equal(FixedNow, presence.UpdatedAtUtc);
        Assert.Equal(1u, presence.Version);
    }

    [Fact]
    public void Constructor_WithEmptyCharacterId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new CharacterSocialPresence(
                characterId: Guid.Empty,
                status: SocialPresenceStatus.Active,
                currentActivityType: LifeActivityType.Idle,
                visibility: SocialPresenceVisibility.Public,
                startedAtUtc: FixedNow,
                updatedAtUtc: FixedNow
            ));
    }

    [Fact]
    public void Constructor_WithInvalidStatusEnum_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CharacterSocialPresence(
                characterId: Guid.NewGuid(),
                status: (SocialPresenceStatus)999,
                currentActivityType: LifeActivityType.Idle,
                visibility: SocialPresenceVisibility.Public,
                startedAtUtc: FixedNow,
                updatedAtUtc: FixedNow
            ));
    }

    [Fact]
    public void Constructor_WithInvalidActivityEnum_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CharacterSocialPresence(
                characterId: Guid.NewGuid(),
                status: SocialPresenceStatus.Active,
                currentActivityType: (LifeActivityType)999,
                visibility: SocialPresenceVisibility.Public,
                startedAtUtc: FixedNow,
                updatedAtUtc: FixedNow
            ));
    }

    [Fact]
    public void Constructor_WithInvalidVisibilityEnum_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CharacterSocialPresence(
                characterId: Guid.NewGuid(),
                status: SocialPresenceStatus.Active,
                currentActivityType: LifeActivityType.Idle,
                visibility: (SocialPresenceVisibility)999,
                startedAtUtc: FixedNow,
                updatedAtUtc: FixedNow
            ));
    }

    [Fact]
    public void Constructor_WithDefaultTimestamps_ThrowsArgumentException()
    {
        var charId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            new CharacterSocialPresence(
                characterId: charId,
                status: SocialPresenceStatus.Active,
                currentActivityType: LifeActivityType.Idle,
                visibility: SocialPresenceVisibility.Public,
                startedAtUtc: default,
                updatedAtUtc: FixedNow
            ));

        Assert.Throws<ArgumentException>(() =>
            new CharacterSocialPresence(
                characterId: charId,
                status: SocialPresenceStatus.Active,
                currentActivityType: LifeActivityType.Idle,
                visibility: SocialPresenceVisibility.Public,
                startedAtUtc: FixedNow,
                updatedAtUtc: default
            ));
    }

    [Fact]
    public void Constructor_StartedAfterUpdated_ThrowsArgumentException()
    {
        var charId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            new CharacterSocialPresence(
                characterId: charId,
                status: SocialPresenceStatus.Active,
                currentActivityType: LifeActivityType.Idle,
                visibility: SocialPresenceVisibility.Public,
                startedAtUtc: FixedNow.AddMinutes(10),
                updatedAtUtc: FixedNow
            ));
    }

    [Fact]
    public void CreateDefault_ProducesStandardActivePresence()
    {
        var charId = Guid.NewGuid();
        var presence = CharacterSocialPresence.CreateDefault(charId, FixedNow);

        Assert.Equal(charId, presence.CharacterId);
        Assert.Equal(SocialPresenceStatus.Active, presence.Status);
        Assert.Equal(LifeActivityType.Idle, presence.CurrentActivityType);
        Assert.Equal(SocialPresenceVisibility.Public, presence.Visibility);
        Assert.Null(presence.TargetType);
        Assert.Null(presence.TargetId);
        Assert.Equal(1u, presence.Version);
        Assert.Equal(FixedNow, presence.StartedAtUtc);
        Assert.Equal(FixedNow, presence.UpdatedAtUtc);
    }

    [Fact]
    public void Activate_FromAwayOrOffline_TransitionsToActiveAndBumpsVersion()
    {
        var charId = Guid.NewGuid();
        var presence = CharacterSocialPresence.CreateDefault(charId, FixedNow);
        presence.SetAway(FixedNow.AddMinutes(1));

        var activateTime = FixedNow.AddMinutes(5);
        presence.Activate(activateTime);

        Assert.Equal(SocialPresenceStatus.Active, presence.Status);
        Assert.Equal(activateTime, presence.UpdatedAtUtc);
        Assert.Equal(3u, presence.Version);
    }

    [Fact]
    public void SetAway_TransitionsToAwayAndBumpsVersion()
    {
        var charId = Guid.NewGuid();
        var presence = CharacterSocialPresence.CreateDefault(charId, FixedNow);

        var awayTime = FixedNow.AddMinutes(1);
        presence.SetAway(awayTime);

        Assert.Equal(SocialPresenceStatus.Away, presence.Status);
        Assert.Equal(awayTime, presence.UpdatedAtUtc);
        Assert.Equal(2u, presence.Version);
    }

    [Fact]
    public void SetOffline_TransitionsToOfflineResetsActivityAndClearsTarget()
    {
        var charId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var presence = new CharacterSocialPresence(
            characterId: charId,
            status: SocialPresenceStatus.Active,
            currentActivityType: LifeActivityType.Socialize,
            visibility: SocialPresenceVisibility.Public,
            startedAtUtc: FixedNow,
            updatedAtUtc: FixedNow,
            targetType: RelationshipTargetType.User,
            targetId: targetId
        );

        var offlineTime = FixedNow.AddMinutes(5);
        presence.SetOffline(offlineTime);

        Assert.Equal(SocialPresenceStatus.Offline, presence.Status);
        Assert.Equal(LifeActivityType.Idle, presence.CurrentActivityType);
        Assert.Null(presence.TargetType);
        Assert.Null(presence.TargetId);
        Assert.Equal(offlineTime, presence.UpdatedAtUtc);
        Assert.Equal(2u, presence.Version);
    }

    [Fact]
    public void UpdateActivity_SetsActivityAndTarget()
    {
        var charId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var presence = CharacterSocialPresence.CreateDefault(charId, FixedNow);

        var updateTime = FixedNow.AddMinutes(2);
        presence.UpdateActivity(LifeActivityType.Socialize, updateTime, RelationshipTargetType.Character, targetId);

        Assert.Equal(LifeActivityType.Socialize, presence.CurrentActivityType);
        Assert.Equal(RelationshipTargetType.Character, presence.TargetType);
        Assert.Equal(targetId, presence.TargetId);
        Assert.Equal(updateTime, presence.UpdatedAtUtc);
        Assert.Equal(2u, presence.Version);
    }

    [Fact]
    public void UpdateActivity_SocializeWhileOffline_ThrowsInvalidOperationException()
    {
        var charId = Guid.NewGuid();
        var presence = CharacterSocialPresence.CreateDefault(charId, FixedNow);
        presence.SetOffline(FixedNow.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() =>
            presence.UpdateActivity(LifeActivityType.Socialize, FixedNow.AddMinutes(2)));
    }

    [Fact]
    public void UpdateVisibility_UpdatesVisibilityAndBumpsVersion()
    {
        var charId = Guid.NewGuid();
        var presence = CharacterSocialPresence.CreateDefault(charId, FixedNow);

        var updateTime = FixedNow.AddMinutes(1);
        presence.UpdateVisibility(SocialPresenceVisibility.Private, updateTime);

        Assert.Equal(SocialPresenceVisibility.Private, presence.Visibility);
        Assert.Equal(updateTime, presence.UpdatedAtUtc);
        Assert.Equal(2u, presence.Version);
    }

    [Fact]
    public void StateMutations_WithTimestampRegression_ThrowsInvalidOperationException()
    {
        var charId = Guid.NewGuid();
        var presence = CharacterSocialPresence.CreateDefault(charId, FixedNow.AddMinutes(10));

        var past = FixedNow; // older than UpdatedAtUtc

        Assert.Throws<InvalidOperationException>(() => presence.Activate(past));
        Assert.Throws<InvalidOperationException>(() => presence.SetAway(past));
        Assert.Throws<InvalidOperationException>(() => presence.SetOffline(past));
        Assert.Throws<InvalidOperationException>(() => presence.UpdateActivity(LifeActivityType.Eat, past));
        Assert.Throws<InvalidOperationException>(() => presence.UpdateVisibility(SocialPresenceVisibility.Private, past));
    }

    [Fact]
    public void ToContext_ProducesAccurateImmutableSnapshot()
    {
        var charId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var presence = new CharacterSocialPresence(
            characterId: charId,
            status: SocialPresenceStatus.Active,
            currentActivityType: LifeActivityType.Work,
            visibility: SocialPresenceVisibility.Private,
            startedAtUtc: FixedNow,
            updatedAtUtc: FixedNow.AddMinutes(5),
            targetType: RelationshipTargetType.User,
            targetId: targetId,
            version: 3
        );

        var context = presence.ToContext();

        Assert.Equal(presence.Id, context.PresenceId);
        Assert.Equal(presence.CharacterId, context.CharacterId);
        Assert.Equal(presence.Status, context.Status);
        Assert.Equal(presence.CurrentActivityType, context.CurrentActivityType);
        Assert.Equal(presence.Visibility, context.Visibility);
        Assert.Equal(presence.StartedAtUtc, context.StartedAtUtc);
        Assert.Equal(presence.UpdatedAtUtc, context.UpdatedAtUtc);
        Assert.Equal(presence.TargetType, context.TargetType);
        Assert.Equal(presence.TargetId, context.TargetId);
        Assert.Equal(presence.Version, context.Version);
    }

    [Fact]
    public void ComputeFingerprint_IsDeterministicAndSensitiveToInputs()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var hash1 = CharacterSocialPresenceTransition.ComputeFingerprint(charId, execId, "Socialize", RelationshipTargetType.User, targetId);
        var hash2 = CharacterSocialPresenceTransition.ComputeFingerprint(charId, execId, "Socialize", RelationshipTargetType.User, targetId);
        var hashDifferentAction = CharacterSocialPresenceTransition.ComputeFingerprint(charId, execId, "Rest", RelationshipTargetType.User, targetId);

        Assert.Equal(hash1, hash2);
        Assert.NotEqual(hash1, hashDifferentAction);
        Assert.Equal(64, hash1.Length); // 256 bits in hex
    }
}
