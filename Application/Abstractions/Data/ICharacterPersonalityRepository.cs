using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;

namespace Application.Abstractions.Data;

public interface ICharacterPersonalityRepository
{
    Task<CharacterPersonality?> GetByCharacterIdAsync(Guid characterId, CancellationToken ct = default);
    Task<CharacterPersonality> GetOrCreateDefaultAsync(Guid characterId, CancellationToken ct = default);
    Task AddAsync(CharacterPersonality personality, CancellationToken ct = default);

    Task<PersonalityAdaptationEvidence?> GetEvidenceByExecutionIdAsync(Guid characterId, Guid executionId, CancellationToken ct = default);
    Task<PersonalityAdaptationEvidence?> GetEvidenceByExecutionAndTraitAsync(Guid characterId, Guid executionId, string traitKey, CancellationToken ct = default);
    Task<PersonalityAdaptationEvidence> AddOrGetEvidenceAsync(PersonalityAdaptationEvidence evidence, CancellationToken ct = default);
    Task<IReadOnlyList<PersonalityAdaptationEvidence>> GetUnappliedEvidenceAsync(Guid characterId, string traitKey, CancellationToken ct = default);

    Task<CharacterPersonalityAdaptation?> GetAdaptationByExecutionIdAsync(Guid characterId, Guid executionId, CancellationToken ct = default);
    Task<CharacterPersonalityAdaptation?> GetAdaptationByExecutionAndTraitAsync(Guid characterId, Guid executionId, string traitKey, CancellationToken ct = default);
    Task AddAdaptationAsync(CharacterPersonalityAdaptation adaptation, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears change tracker state to prevent stale or detached entities from contaminating retries.
    /// </summary>
    void ClearTracking();
}
