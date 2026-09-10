using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface ICharacterVisualProfileReader
{
    Task<CharacterVisualProfile?> GetProfileByCharacterIdAsync(Guid characterId, CancellationToken ct = default);
    Task<CharacterVisualIdentity?> GetVisualIdentityByCharacterIdAsync(Guid characterId, CancellationToken ct = default);
}
