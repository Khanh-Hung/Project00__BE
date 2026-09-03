using Domain.ValueObjects;

namespace Domain.Policies;

public interface ICharacterEmotionPolicy
{
    CharacterEmotion Evaluate(
        CharacterAppraisal appraisal,
        CharacterBlueprint? blueprint = null);
}
