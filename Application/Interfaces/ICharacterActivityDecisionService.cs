using Application.Contracts.Activities;

namespace Application.Interfaces;

/// <summary>
/// Authoritative contract for evaluating and selecting a character's next autonomous activity.
/// Guaranteed to be strictly deterministic for identical inputs.
/// </summary>
public interface ICharacterActivityDecisionService
{
    Task<CharacterActivityCandidate?> DecideAsync(
        CharacterActivityDecisionRequest request,
        CancellationToken ct = default);
}
