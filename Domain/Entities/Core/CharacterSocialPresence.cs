using System;
using Domain.Common;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Entities;

/// <summary>
/// Authoritative persistent domain aggregate representing a character's social presence.
/// Enforces database-level uniqueness per CharacterId.
/// Uses Version concurrency token for optimistic concurrency control.
/// </summary>
public sealed class CharacterSocialPresence : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public SocialPresenceStatus Status { get; private set; }
    public LifeActivityType CurrentActivityType { get; private set; }
    public RelationshipTargetType? TargetType { get; private set; }
    public Guid? TargetId { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public SocialPresenceVisibility Visibility { get; private set; }
    public uint Version { get; private set; } = 1;

    private CharacterSocialPresence() : base() { } // EF Core

    public CharacterSocialPresence(
        Guid characterId,
        SocialPresenceStatus status,
        LifeActivityType currentActivityType,
        SocialPresenceVisibility visibility,
        DateTimeOffset startedAtUtc,
        DateTimeOffset updatedAtUtc,
        RelationshipTargetType? targetType = null,
        Guid? targetId = null,
        uint version = 1,
        Guid? id = null) : base(id ?? Guid.CreateVersion7())
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (!Enum.IsDefined(typeof(SocialPresenceStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Invalid SocialPresenceStatus.");

        if (!Enum.IsDefined(typeof(LifeActivityType), currentActivityType))
            throw new ArgumentOutOfRangeException(nameof(currentActivityType), currentActivityType, "Invalid LifeActivityType.");

        if (!Enum.IsDefined(typeof(SocialPresenceVisibility), visibility))
            throw new ArgumentOutOfRangeException(nameof(visibility), visibility, "Invalid SocialPresenceVisibility.");

        if (startedAtUtc == default)
            throw new ArgumentException("StartedAtUtc cannot be default.", nameof(startedAtUtc));

        if (updatedAtUtc == default)
            throw new ArgumentException("UpdatedAtUtc cannot be default.", nameof(updatedAtUtc));

        if (startedAtUtc > updatedAtUtc)
            throw new ArgumentException("StartedAtUtc cannot be after UpdatedAtUtc.", nameof(startedAtUtc));

        CharacterId = characterId;
        Status = status;
        CurrentActivityType = currentActivityType;
        Visibility = visibility;
        StartedAtUtc = startedAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        TargetType = targetType;
        TargetId = targetId;
        Version = version == 0 ? 1u : version;
    }

    public static CharacterSocialPresence CreateDefault(Guid characterId, DateTimeOffset now)
    {
        return new CharacterSocialPresence(
            characterId: characterId,
            status: SocialPresenceStatus.Active,
            currentActivityType: LifeActivityType.Idle,
            visibility: SocialPresenceVisibility.Public,
            startedAtUtc: now,
            updatedAtUtc: now,
            targetType: null,
            targetId: null,
            version: 1
        );
    }

    public void Activate(DateTimeOffset now)
    {
        if (now < UpdatedAtUtc)
            throw new InvalidOperationException($"Timestamp {now:O} cannot be older than current UpdatedAtUtc {UpdatedAtUtc:O}.");

        Status = SocialPresenceStatus.Active;
        UpdatedAtUtc = now;
        Version++;
    }

    public void SetAway(DateTimeOffset now)
    {
        if (now < UpdatedAtUtc)
            throw new InvalidOperationException($"Timestamp {now:O} cannot be older than current UpdatedAtUtc {UpdatedAtUtc:O}.");

        Status = SocialPresenceStatus.Away;
        UpdatedAtUtc = now;
        Version++;
    }

    public void SetOffline(DateTimeOffset now)
    {
        if (now < UpdatedAtUtc)
            throw new InvalidOperationException($"Timestamp {now:O} cannot be older than current UpdatedAtUtc {UpdatedAtUtc:O}.");

        Status = SocialPresenceStatus.Offline;
        CurrentActivityType = LifeActivityType.Idle;
        TargetType = null;
        TargetId = null;
        UpdatedAtUtc = now;
        Version++;
    }

    public void UpdateActivity(
        LifeActivityType activityType,
        DateTimeOffset now,
        RelationshipTargetType? targetType = null,
        Guid? targetId = null)
    {
        if (!Enum.IsDefined(typeof(LifeActivityType), activityType))
            throw new ArgumentOutOfRangeException(nameof(activityType), activityType, "Invalid LifeActivityType.");

        if (now < UpdatedAtUtc)
            throw new InvalidOperationException($"Timestamp {now:O} cannot be older than current UpdatedAtUtc {UpdatedAtUtc:O}.");

        if (Status == SocialPresenceStatus.Offline && activityType == LifeActivityType.Socialize)
            throw new InvalidOperationException("Cannot engage in Socialize activity while Offline.");

        CurrentActivityType = activityType;
        TargetType = targetType;
        TargetId = targetId;
        UpdatedAtUtc = now;
        Version++;
    }

    public void UpdateVisibility(SocialPresenceVisibility visibility, DateTimeOffset now)
    {
        if (!Enum.IsDefined(typeof(SocialPresenceVisibility), visibility))
            throw new ArgumentOutOfRangeException(nameof(visibility), visibility, "Invalid SocialPresenceVisibility.");

        if (now < UpdatedAtUtc)
            throw new InvalidOperationException($"Timestamp {now:O} cannot be older than current UpdatedAtUtc {UpdatedAtUtc:O}.");

        Visibility = visibility;
        UpdatedAtUtc = now;
        Version++;
    }

    public CharacterSocialPresenceContext ToContext() => new(
        PresenceId: Id,
        CharacterId: CharacterId,
        Status: Status,
        CurrentActivityType: CurrentActivityType,
        TargetType: TargetType,
        TargetId: TargetId,
        Visibility: Visibility,
        StartedAtUtc: StartedAtUtc,
        UpdatedAtUtc: UpdatedAtUtc,
        Version: Version
    );
}
