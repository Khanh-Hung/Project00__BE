using Domain.ValueObjects;

namespace Domain.Policies;

public interface ICharacterInternalExperiencePolicy
{
    CharacterInternalExperience Evaluate(
        CharacterStateSnapshot state,
        CharacterPerceptionContext context,
        PsychologyProfile? psychology = null);
}
