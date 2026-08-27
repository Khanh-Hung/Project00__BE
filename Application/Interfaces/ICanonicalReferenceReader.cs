using Domain.Entities;

namespace Application.Interfaces;

public interface ICanonicalReferenceReader
{
    Task<CharacterVisualReference?> GetActiveCanonicalReferenceAsync(Guid characterId, CancellationToken ct = default);
}
