using Application.DTOs;
using Domain.Entities;
using Domain.Enums;

namespace Application.Interfaces;

public interface ICharacterVisualReferenceService
{
    Task<CharacterVisualReference> RegisterReferenceAsync(RegisterVisualReferenceRequest request, CancellationToken ct = default);
    Task<CharacterVisualReference> PromoteToCanonicalAsync(Guid characterId, Guid referenceId, CancellationToken ct = default);
    Task<CharacterVisualReference> DemoteCanonicalAsync(Guid characterId, Guid referenceId, CancellationToken ct = default);
    Task<CharacterVisualReference> ArchiveReferenceAsync(Guid characterId, Guid referenceId, CancellationToken ct = default);
    Task<IReadOnlyList<CharacterVisualReference>> ListReferencesAsync(Guid characterId, VisualReferenceStatus? status = null, CancellationToken ct = default);
    Task<CharacterVisualReference?> GetPrimaryCanonicalReferenceAsync(Guid characterId, CancellationToken ct = default);
}
