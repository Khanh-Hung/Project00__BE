using Domain.ValueObjects;

namespace Domain.Policies;

public interface ICharacterDesirePolicy
{
    CharacterDesireEvaluation Evaluate(
        CharacterInternalExperience experience,
        CharacterAppraisal appraisal,
        CharacterEmotion emotion);
}
