using Application.Common;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface ICharacterStateService
{
    Task<CharacterStateSnapshot?> GetAsync(
        Guid characterId,
        CancellationToken ct = default);

    Task<CharacterStateSnapshot> GetOrCreateInitialStateAsync(
        Guid characterId,
        DateTime nowUtc,
        CancellationToken ct = default);

    Task<StateTransitionResult> ApplyDeltaAsync(
        Guid characterId,
        CharacterStateDelta delta,
        StateTransitionContext context,
        DateTime nowUtc,
        CancellationToken ct = default);

    Task<StateTransitionResult> EvolveToAsync(
        Guid characterId,
        DateTime nowUtc,
        CancellationToken ct = default);
}
