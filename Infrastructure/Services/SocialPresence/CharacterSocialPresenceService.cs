using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.SocialPresence;
using Application.Interfaces;
using Domain.Enums;

namespace Infrastructure.Services.SocialPresence;

public sealed class CharacterSocialPresenceService : ICharacterSocialPresenceService
{
    private readonly ICharacterSocialPresenceRepository _repository;
    private readonly ISocialPresenceTransitionService _transitionService;

    public CharacterSocialPresenceService(
        ICharacterSocialPresenceRepository repository,
        ISocialPresenceTransitionService transitionService)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _transitionService = transitionService ?? throw new ArgumentNullException(nameof(transitionService));
    }

    public async Task<CharacterSocialPresenceDto?> GetPresenceAsync(Guid characterId, CancellationToken ct = default)
    {
        var presence = await _repository.GetByCharacterIdAsync(characterId, ct);
        if (presence == null) return null;

        return new CharacterSocialPresenceDto(
            PresenceId: presence.Id,
            CharacterId: presence.CharacterId,
            Status: presence.Status,
            CurrentActivityType: presence.CurrentActivityType,
            Visibility: presence.Visibility,
            StartedAtUtc: presence.StartedAtUtc,
            UpdatedAtUtc: presence.UpdatedAtUtc,
            TargetType: presence.TargetType,
            TargetId: presence.TargetId,
            Version: presence.Version
        );
    }

    public async Task<CharacterSocialPresenceDto> SetStatusAsync(
        Guid characterId,
        SocialPresenceStatus status,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var updated = status switch
        {
            SocialPresenceStatus.Active => await _transitionService.ActivateAsync(characterId, now, ct),
            SocialPresenceStatus.Away => await _transitionService.SetAwayAsync(characterId, now, ct),
            SocialPresenceStatus.Offline => await _transitionService.SetOfflineAsync(characterId, now, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported SocialPresenceStatus.")
        };

        return new CharacterSocialPresenceDto(
            PresenceId: updated.Id,
            CharacterId: updated.CharacterId,
            Status: updated.Status,
            CurrentActivityType: updated.CurrentActivityType,
            Visibility: updated.Visibility,
            StartedAtUtc: updated.StartedAtUtc,
            UpdatedAtUtc: updated.UpdatedAtUtc,
            TargetType: updated.TargetType,
            TargetId: updated.TargetId,
            Version: updated.Version
        );
    }

    public async Task<CharacterSocialPresenceDto> SetVisibilityAsync(
        Guid characterId,
        SocialPresenceVisibility visibility,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var updated = await _transitionService.SetVisibilityAsync(characterId, visibility, now, ct);

        return new CharacterSocialPresenceDto(
            PresenceId: updated.Id,
            CharacterId: updated.CharacterId,
            Status: updated.Status,
            CurrentActivityType: updated.CurrentActivityType,
            Visibility: updated.Visibility,
            StartedAtUtc: updated.StartedAtUtc,
            UpdatedAtUtc: updated.UpdatedAtUtc,
            TargetType: updated.TargetType,
            TargetId: updated.TargetId,
            Version: updated.Version
        );
    }
}
