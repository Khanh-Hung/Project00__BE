using Application.DTOs;
using Domain.Entities;

namespace Application.Interfaces;

public interface ICharacterVisualProfileService
{
    Task<CharacterVisualProfile?> GetCurrentProfileAsync(Guid characterId, CancellationToken ct = default);
    Task<CharacterVisualProfile> CreateProfileAsync(Guid characterId, string? hair = null, string? eye = null, string? skin = null, string? body = null, string? features = null, CancellationToken ct = default);
    Task<CharacterVisualProfile> UpdateAppearanceAsync(Guid characterId, string? hair, string? eye, string? skin, string? body, string? features, CancellationToken ct = default);
    Task<CharacterVisualProfile> SetPrimaryReferenceAsync(Guid characterId, Guid referenceId, CancellationToken ct = default);
    Task<CharacterVisualProfile> SetFaceReferenceAsync(Guid characterId, Guid referenceId, CancellationToken ct = default);
}
