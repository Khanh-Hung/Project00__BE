using System;
using Domain.Enums;

namespace Application.Contracts.SocialPresence;

public sealed record CharacterSocialPresenceDto(
    Guid PresenceId,
    Guid CharacterId,
    SocialPresenceStatus Status,
    LifeActivityType CurrentActivityType,
    SocialPresenceVisibility Visibility,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RelationshipTargetType? TargetType = null,
    Guid? TargetId = null,
    uint Version = 1
);
