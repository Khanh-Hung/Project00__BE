using Domain.ValueObjects;

namespace Domain.Policies;

public interface ICharacterStateEvolutionPolicy
{
    CharacterStateDelta CalculateEvolutionDelta(
        CharacterStateSnapshot currentState,
        DateTime lastEvolvedAtUtc,
        DateTime nowUtc);
}
