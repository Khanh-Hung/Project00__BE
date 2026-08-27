using Application.DTOs;

namespace Application.Interfaces;

public interface ICharacterVisualReferenceResolver
{
    Task<VisualReferenceSet> ResolveAsync(Guid characterId, VisualReferenceContext context, CancellationToken ct = default);
}
