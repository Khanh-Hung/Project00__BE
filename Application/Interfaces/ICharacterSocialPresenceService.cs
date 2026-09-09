using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.SocialPresence;
using Domain.Enums;

namespace Application.Interfaces;

public interface ICharacterSocialPresenceService
{
    Task<CharacterSocialPresenceDto?> GetPresenceAsync(Guid characterId, CancellationToken ct = default);
    Task<CharacterSocialPresenceDto> SetStatusAsync(Guid characterId, SocialPresenceStatus status, DateTimeOffset now, CancellationToken ct = default);
    Task<CharacterSocialPresenceDto> SetVisibilityAsync(Guid characterId, SocialPresenceVisibility visibility, DateTimeOffset now, CancellationToken ct = default);
}
