using Domain.ValueObjects;

namespace Domain.Policies;

public interface ICharacterPerceptionPolicy
{
    CharacterInternalExperience Evaluate(
        CharacterStateSnapshot state,
        PsychologyProfile? psychology = null,
        CharacterPerceptionContext? context = null);
}
