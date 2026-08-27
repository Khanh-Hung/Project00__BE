using Domain.Entities;

namespace Application.Interfaces;

public interface ICharacterVisualProfileReader
{
    Task<CharacterVisualProfile?> GetProfileByCharacterIdAsync(Guid characterId, CancellationToken ct = default);
}
