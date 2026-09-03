using Domain.ValueObjects;

namespace Domain.Policies;

public interface ICharacterEmotionPolicy
{
    CharacterEmotion Evaluate(
        CharacterAppraisal appraisal,
        CharacterBlueprint? blueprint = null);

    CharacterEmotion Evaluate(
        CharacterInternalExperience experience,
        CharacterAppraisal appraisal,
        CharacterBlueprint? blueprint = null);

    CharacterEmotion EvaluateDominant(
        CharacterInternalExperience experience,
        CharacterBlueprint? blueprint = null);
}
