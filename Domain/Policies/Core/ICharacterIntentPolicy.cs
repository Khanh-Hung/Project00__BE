using Domain.ValueObjects;

namespace Domain.Policies;

public interface ICharacterIntentPolicy
{
    CharacterIntentEvaluation Evaluate(
        CharacterDesireEvaluation desireEvaluation,
        CharacterIntentContext context);
}
