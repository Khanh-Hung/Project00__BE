using Domain.Entities;

namespace Application.Interfaces;

public interface ICharacterVisualProfileService
{
    Task<CharacterVisualProfile?> GetCurrentProfileAsync(Guid characterId, CancellationToken ct = default);

    Task<CharacterVisualProfile> CreateProfileAsync(
        Guid characterId,
        string? eyeColor = null,
        string? hairColor = null,
        string? skinTone = null,
        string? facialFeatures = null,
        string? permanentMarks = null,
        string? bodyIdentity = null,
        string? hairstyle = null,
        string? currentOutfit = null,
        string? makeup = null,
        string? accessories = null,
        string? temporaryAppearance = null,
        CancellationToken ct = default);

    Task<CharacterVisualProfile> UpdateAppearanceAsync(
        Guid characterId,
        string? hairstyle = null,
        string? currentOutfit = null,
        string? makeup = null,
        string? accessories = null,
        string? temporaryAppearance = null,
        CancellationToken ct = default);

    Task<CharacterVisualProfile> RefineCoreIdentityAsync(
        Guid characterId,
        string? eyeColor = null,
        string? hairColor = null,
        string? skinTone = null,
        string? facialFeatures = null,
        string? permanentMarks = null,
        string? bodyIdentity = null,
        CancellationToken ct = default);

    Task<CharacterVisualProfile> SetPrimaryReferenceAsync(
        Guid characterId,
        Guid referenceId,
        CancellationToken ct = default);

    Task<CharacterVisualProfile> SetFaceReferenceAsync(
        Guid characterId,
        Guid referenceId,
        CancellationToken ct = default);
}
